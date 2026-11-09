import { createApp } from 'vue'
import { Quasar, Notify } from 'quasar'

import '@quasar/extras/material-icons/material-icons.css'
import 'quasar/src/css/index.sass'

import App from './App.vue'

createApp(App)
  .use(Quasar, {
    plugins: { Notify },
    config: {
      notify: { position: 'bottom', timeout: 2600 },
      brand: { primary: '#2563eb', positive: '#16a34a', negative: '#dc2626' },
    },
  })
  .mount('#app')
