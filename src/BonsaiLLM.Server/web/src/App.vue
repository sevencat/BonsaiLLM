<script setup>
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { Notify } from 'quasar'
import { marked } from 'marked'
import DOMPurify from 'dompurify'

marked.setOptions({ breaks: true, gfm: true })

const state = ref(null)
const messages = ref([])
const draft = ref('')
const imageData = ref(null)
const streaming = ref(false)
const selected = ref('')
const settingsOpen = ref(false)
const scroller = ref(null)
const fileInput = ref(null)
const params = reactive({ temperature: 0.7, topP: 0.9, maxTokens: -1 })

let abort = null
let pollTimer = null

const models = computed(() => state.value?.models ?? [])
const loaded = computed(() => state.value?.loaded ?? null)
const loading = computed(() => state.value?.loading === true)
const current = computed(() => models.value.find((m) => m.id === selected.value) ?? null)
const needLoad = computed(() => current.value && (!loaded.value || loaded.value.id !== current.value.id))
const options = computed(() =>
  models.value.map((m) => ({
    label: m.id,
    value: m.id,
    caption: [m.quantization, m.sizeText].filter(Boolean).join(' · ') || '未读到元数据',
    vision: m.hasVision,
    exists: m.fileExists,
    path: m.path,
  })),
)

watch(state, (value) => {
  if (value && !selected.value && value.models.length) {
    selected.value = value.loaded?.id ?? value.models[0].id
  }
})

async function refresh() {
  try {
    const response = await fetch('/api/state')
    if (response.ok) state.value = await response.json()
  } catch {
    /* Server 未就绪时静默 */
  }
}

async function loadSelected() {
  if (!current.value) return
  try {
    const response = await fetch('/api/load', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ model: current.value.id }),
    })
    if (!response.ok && response.status !== 409) {
      const detail = await response.json().catch(() => null)
      throw new Error(detail?.message ?? `加载失败（${response.status}）`)
    }
    await refresh()
  } catch (error) {
    Notify.create({ message: error.message, color: 'negative' })
  }
}

function scrollToBottom() {
  nextTick(() => {
    if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight
  })
}

function render(text) {
  return text ? DOMPurify.sanitize(marked.parse(text)) : ''
}

function pickImage() {
  fileInput.value?.click()
}

async function onFile(event) {
  const file = event.target.files?.[0]
  event.target.value = ''
  if (!file) return
  if (!current.value?.hasVision) {
    Notify.create({ message: '当前模型没有 mmproj，换视觉模型再用图', color: 'warning' })
    return
  }
  try {
    imageData.value = await compress(file)
  } catch {
    Notify.create({ message: '这张图读不出来', color: 'negative' })
  }
}

// 浏览器先缩到长边 1600 再转 jpeg，Server 只收 base64
async function compress(file) {
  const bitmap = await createImageBitmap(file)
  const scale = Math.min(1, 1600 / Math.max(bitmap.width, bitmap.height))
  const canvas = document.createElement('canvas')
  canvas.width = Math.round(bitmap.width * scale)
  canvas.height = Math.round(bitmap.height * scale)
  canvas.getContext('2d').drawImage(bitmap, 0, 0, canvas.width, canvas.height)
  bitmap.close?.()
  return canvas.toDataURL('image/jpeg', 0.9)
}

function clearChat() {
  messages.value = []
}

async function stop() {
  abort?.abort()
  await fetch('/api/stop', { method: 'POST' }).catch(() => {})
}

function buildBody() {
  const list = messages.value.map((m) =>
    m.image
      ? {
          role: m.role,
          content: [
            { type: 'text', text: m.content || '描述这张图片。' },
            { type: 'image_url', image_url: { url: m.image } },
          ],
        }
      : { role: m.role, content: m.content },
  )
  const body = { model: current.value.id, stream: true, temperature: params.temperature, top_p: params.topP, messages: list }
  if (params.maxTokens > 0) body.max_tokens = params.maxTokens
  return body
}

async function send() {
  const text = draft.value.trim()
  if (streaming.value || (!text && !imageData.value)) return
  if (!current.value) {
    Notify.create({ message: '先选一个模型', color: 'warning' })
    return
  }

  // 接口不会自己换模型，换模型只能在这儿点；先加载，加载完再发
  if (needLoad.value) {
    Notify.create({ message: `正在加载 ${current.value.id}，加载完再点一次发送`, color: 'info' })
    await loadSelected()
    return
  }

  messages.value.push({ role: 'user', content: text, image: imageData.value })
  draft.value = ''
  imageData.value = null

  const assistant = reactive({ role: 'assistant', content: '', meta: '' })
  messages.value.push(assistant)
  scrollToBottom()

  const started = performance.now()
  let firstAt = 0
  abort = new AbortController()
  streaming.value = true

  try {
    const response = await fetch('/v1/chat/completions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(buildBody()),
      signal: abort.signal,
    })

    if (!response.ok) {
      const detail = await response.json().catch(() => null)
      throw new Error(detail?.error?.message ?? `HTTP ${response.status}`)
    }

    const reader = response.body.getReader()
    const decoder = new TextDecoder()
    let buffer = ''

    for (;;) {
      const { value, done } = await reader.read()
      if (done) break
      buffer += decoder.decode(value, { stream: true })

      let boundary
      while ((boundary = buffer.indexOf('\n\n')) >= 0) {
        const frame = buffer.slice(0, boundary)
        buffer = buffer.slice(boundary + 2)
        const data = frame
          .split('\n')
          .filter((line) => line.startsWith('data:'))
          .map((line) => line.slice(5).trim())
          .join('')
        if (!data || data === '[DONE]') continue

        let parsed
        try {
          parsed = JSON.parse(data)
        } catch {
          continue
        }
        if (parsed.error) throw new Error(parsed.error.message)

        const delta = parsed.choices?.[0]?.delta?.content
        if (delta) {
          if (!firstAt) firstAt = performance.now()
          assistant.content += delta
          scrollToBottom()
        }
      }
    }

    const seconds = (performance.now() - started) / 1000
    const ttft = firstAt ? ((firstAt - started) / 1000).toFixed(2) : '—'
    assistant.meta = `首字 ${ttft}s · 总计 ${seconds.toFixed(1)}s`
    if (!assistant.content) assistant.content = '（模型没有返回内容）'
  } catch (error) {
    if (error.name === 'AbortError') {
      assistant.meta = '已停止'
      if (!assistant.content) assistant.content = '（已停止）'
    } else {
      assistant.content = assistant.content || `出错了：${error.message}`
      Notify.create({ message: error.message, color: 'negative' })
    }
  } finally {
    streaming.value = false
    abort = null
    scrollToBottom()
  }
}

onMounted(() => {
  refresh()
  pollTimer = setInterval(refresh, 1500)
})

onUnmounted(() => clearInterval(pollTimer))
</script>

<template>
  <div class="page">
    <header class="topbar">
      <div class="brand">BonsaiLLM <small>server</small></div>

      <q-select
        v-model="selected"
        :options="options"
        emit-value
        map-options
        dense
        outlined
        options-dense
        class="picker"
      >
        <template #option="scope">
          <q-item v-bind="scope.itemProps">
            <q-item-section>
              <q-item-label>
                {{ scope.opt.label }}
                <q-badge v-if="scope.opt.vision" color="blue-1" text-color="blue-9" class="q-ml-xs">视觉</q-badge>
              </q-item-label>
              <q-item-label caption>{{ scope.opt.caption }}</q-item-label>
            </q-item-section>
            <q-item-section v-if="!scope.opt.exists" side>
              <q-icon name="error_outline" color="negative" size="18px">
                <q-tooltip>文件不存在：{{ scope.opt.path }}</q-tooltip>
              </q-icon>
            </q-item-section>
          </q-item>
        </template>
      </q-select>

      <q-btn v-if="needLoad" label="加载" unelevated color="primary" size="sm" :loading="loading" @click="loadSelected" />

      <q-space />

      <span class="status text-caption">
        <template v-if="loaded">{{ loaded.contextSize }} ctx · {{ loaded.tokensUsed }} tok</template>
        <template v-else>未加载模型</template>
        · {{ state?.backend ?? '—' }}
      </span>

      <q-btn flat round dense icon="delete_sweep" :disable="!messages.length" @click="clearChat">
        <q-tooltip>清空会话</q-tooltip>
      </q-btn>
      <q-btn flat round dense icon="tune" @click="settingsOpen = true">
        <q-tooltip>参数</q-tooltip>
      </q-btn>
    </header>

    <q-linear-progress
      v-if="loading || streaming"
      :value="loading ? state.loadProgress : 0.02"
      :indeterminate="streaming && !loading"
      color="primary"
      size="2px"
    />

    <main ref="scroller" class="chat">
      <div class="chat-inner">
        <div v-if="!messages.length" class="empty">
          <div class="empty-title">本地小模型，跑完就扔</div>
          <p>选模型 → 需要时点「加载」→ 直接在下面说话。</p>
          <p>接口是 OpenAI 兼容的：<code>/v1/chat/completions</code>、<code>/v1/models</code>。</p>
          <p>模型与参数写在 <code>appsettings.json</code>，改完重启 Server 生效。</p>
        </div>

        <div v-for="(m, i) in messages" :key="i" class="msg" :class="m.role">
          <div class="avatar">{{ m.role === 'user' ? '你' : 'AI' }}</div>
          <div class="bubble">
            <img v-if="m.image" :src="m.image" class="shot" alt="附件" />
            <div v-if="m.role === 'assistant'" class="body markdown" v-html="render(m.content)" />
            <div v-else class="body plain">{{ m.content }}</div>
            <div v-if="m.meta" class="meta">{{ m.meta }}</div>
          </div>
        </div>
      </div>
    </main>

    <footer class="composer">
      <div class="composer-inner">
        <div v-if="imageData" class="attach">
          <img :src="imageData" alt="待发送" />
          <q-btn round dense flat icon="close" size="sm" @click="imageData = null" />
        </div>

        <div class="row items-end no-wrap q-gutter-sm">
          <q-btn flat round dense icon="image" @click="pickImage">
            <q-tooltip>插入图片</q-tooltip>
          </q-btn>

          <q-input
            v-model="draft"
            type="textarea"
            autogrow
            outlined
            dense
            class="col"
            :disable="streaming"
            placeholder="问点什么…（Enter 发送，Shift+Enter 换行）"
            @keydown.enter.exact.prevent="send"
          />

          <q-btn v-if="streaming" round unelevated color="negative" icon="stop" @click="stop" />
          <q-btn
            v-else
            round
            unelevated
            color="primary"
            icon="send"
            :disable="(!draft.trim() && !imageData) || !current"
            @click="send"
          />
        </div>

        <input ref="fileInput" type="file" accept="image/*" hidden @change="onFile" />
      </div>
    </footer>

    <q-dialog v-model="settingsOpen">
      <q-card class="settings">
        <q-card-section class="text-subtitle1">生成参数</q-card-section>
        <q-card-section class="q-gutter-y-md">
          <div>
            <div class="row justify-between text-caption text-grey-7">
              <span>温度</span><span>{{ params.temperature.toFixed(2) }}</span>
            </div>
            <q-slider v-model="params.temperature" :min="0" :max="2" :step="0.05" color="primary" />
          </div>
          <div>
            <div class="row justify-between text-caption text-grey-7">
              <span>top_p</span><span>{{ params.topP.toFixed(2) }}</span>
            </div>
            <q-slider v-model="params.topP" :min="0.1" :max="1" :step="0.05" color="primary" />
          </div>
          <q-input v-model.number="params.maxTokens" type="number" dense outlined label="max_tokens（≤0 不限）" />
          <div class="text-caption text-grey-7">只影响本次请求，默认值来自 appsettings.json。</div>
          <div v-if="current" class="path">{{ current.path }}</div>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat label="关闭" v-close-popup />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>

<style>
body {
  margin: 0;
  font-family: 'Segoe UI', 'Microsoft YaHei', system-ui, sans-serif;
}
</style>

<style scoped>
.page {
  display: flex;
  flex-direction: column;
  height: 100vh;
  background: #f6f7fb;
  color: #0f172a;
}

.topbar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 16px;
  background: #fff;
  border-bottom: 1px solid #e2e8f0;
}

.brand {
  font-weight: 700;
  font-size: 15px;
  white-space: nowrap;
}

.brand small {
  font-weight: 400;
  font-size: 11px;
  color: #94a3b8;
}

.picker {
  min-width: 240px;
  max-width: 320px;
}

.status {
  color: #64748b;
  white-space: nowrap;
}

.chat {
  flex: 1;
  overflow-y: auto;
  padding: 18px 16px 6px;
}

.chat-inner,
.composer-inner {
  max-width: 860px;
  margin: 0 auto;
}

.empty {
  max-width: 560px;
  margin: 12vh auto 0;
  color: #64748b;
  font-size: 13px;
  line-height: 1.7;
  text-align: center;
}

.empty-title {
  font-size: 18px;
  font-weight: 600;
  color: #0f172a;
  margin-bottom: 4px;
}

.empty code,
.settings code {
  background: #eef2f7;
  border-radius: 4px;
  padding: 1px 5px;
  font-size: 12px;
}

.msg {
  display: flex;
  gap: 10px;
  margin-bottom: 14px;
  align-items: flex-start;
}

.msg.user {
  flex-direction: row-reverse;
}

.avatar {
  flex: none;
  width: 28px;
  height: 28px;
  margin-top: 2px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 11px;
  font-weight: 600;
  background: #e0f2fe;
  color: #0369a1;
}

.msg.user .avatar {
  background: #2563eb;
  color: #fff;
}

.bubble {
  max-width: min(680px, 76%);
  padding: 9px 13px;
  border-radius: 12px;
  background: #fff;
  border: 1px solid #e2e8f0;
  box-shadow: 0 1px 2px rgba(15, 23, 42, 0.04);
  font-size: 14px;
  line-height: 1.62;
}

.msg.user .bubble {
  background: #2563eb;
  border-color: transparent;
  color: #fff;
}

.bubble.plain,
.bubble > .body {
  white-space: pre-wrap;
  word-break: break-word;
}

.shot {
  display: block;
  max-width: 100%;
  max-height: 320px;
  border-radius: 8px;
  margin-bottom: 8px;
}

.meta {
  margin-top: 6px;
  font-size: 11px;
  color: #94a3b8;
}

.markdown :deep(p) {
  margin: 0 0 8px;
}

.markdown :deep(p:last-child) {
  margin-bottom: 0;
}

.markdown :deep(pre) {
  background: #0f172a;
  color: #e2e8f0;
  padding: 10px 12px;
  border-radius: 8px;
  overflow-x: auto;
  font-size: 12px;
  line-height: 1.5;
}

.markdown :deep(code) {
  font-family: ui-monospace, Consolas, monospace;
}

.markdown :deep(ul),
.markdown :deep(ol) {
  padding-left: 20px;
  margin: 0 0 8px;
}

.composer {
  background: #fff;
  border-top: 1px solid #e2e8f0;
  padding: 10px 16px 12px;
}

.attach {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 8px;
  padding: 4px 6px;
  border: 1px solid #e2e8f0;
  border-radius: 10px;
  background: #f8fafc;
}

.attach img {
  height: 48px;
  border-radius: 6px;
}

.settings {
  width: 400px;
  max-width: 92vw;
}

.path {
  font-size: 11px;
  color: #94a3b8;
  word-break: break-all;
}
</style>
