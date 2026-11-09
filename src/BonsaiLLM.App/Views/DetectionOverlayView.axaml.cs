using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using BonsaiLLM.App.ViewModels;

namespace BonsaiLLM.App.Views;

/// <summary>
/// 在图片上叠加检测框。坐标统一按 0~1 归一化，画布固定成图片像素尺寸，
/// 外层用 Viewbox 缩放，所以框和图片始终对齐。
/// 标签只保留一个浮动实例，避免几十个框各挂一个标签把图糊住。
/// </summary>
public partial class DetectionOverlayView : UserControl
{
    private static readonly IBrush StrokeBrush = new SolidColorBrush(Color.Parse("#E53E3E"));

    private static readonly IBrush FillBrush = new SolidColorBrush(Color.Parse("#24E53E3E"));

    private static readonly IBrush HoverFillBrush = new SolidColorBrush(Color.Parse("#5AE53E3E"));

    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.Parse("#E53E3E"));

    private readonly Grid? _surface;
    private readonly Image? _image;
    private readonly Canvas? _layer;

    private ChatMessageViewModel? _vm;
    private Border? _tooltip;
    private TextBlock? _tooltipText;

    public DetectionOverlayView()
    {
        InitializeComponent();
        _surface = this.FindControl<Grid>("PART_Surface");
        _image = this.FindControl<Image>("PART_Image");
        _layer = this.FindControl<Canvas>("PART_Layer");

        if (_layer is not null)
        {
            _layer.PointerMoved += OnLayerPointerMoved;
            _layer.PointerExited += (_, _) => HideTooltip();
        }

        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = DataContext as ChatMessageViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        Refresh();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatMessageViewModel.Boxes) or nameof(ChatMessageViewModel.ShowDetection))
        {
            Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
        }
    }

    private void Refresh()
    {
        if (_surface is null || _image is null || _layer is null)
        {
            return;
        }

        _layer.Children.Clear();
        _tooltip = null;
        _tooltipText = null;

        if (_vm?.Image is not { } bitmap)
        {
            return;
        }

        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;

        _image.Source = bitmap;
        _surface.Width = width;
        _surface.Height = height;

        var stroke = Math.Max(2.0, Math.Min(width, height) / 320.0);

        foreach (var box in _vm.Boxes)
        {
            var left = box.X1 * width;
            var top = box.Y1 * height;
            var frameWidth = Math.Max(1, (box.X2 - box.X1) * width);
            var frameHeight = Math.Max(1, (box.Y2 - box.Y1) * height);

            var frame = new Border
            {
                Width = frameWidth,
                Height = frameHeight,
                BorderBrush = StrokeBrush,
                BorderThickness = new Thickness(stroke),
                Background = FillBrush,
                CornerRadius = new CornerRadius(2),
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(frame, left);
            Canvas.SetTop(frame, top);
            _layer.Children.Add(frame);
        }

        AddTooltip(width);
    }

    private void AddTooltip(int imageWidth)
    {
        if (_layer is null)
        {
            return;
        }

        var labelSize = Math.Max(11.0, imageWidth / 70.0);

        _tooltipText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = labelSize,
            FontWeight = FontWeight.SemiBold,
        };

        _tooltip = new Border
        {
            Background = LabelBackground,
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(labelSize * 0.5, 1, labelSize * 0.5, 1),
            Child = _tooltipText,
            IsVisible = false,
            IsHitTestVisible = false,
        };

        _layer.Children.Add(_tooltip);
    }

    private void OnLayerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_vm is null || _surface is null || _tooltip is null || _tooltipText is null)
        {
            return;
        }

        var position = e.GetPosition(_surface);
        var width = _surface.Width;
        var height = _surface.Height;

        foreach (var box in _vm.Boxes)
        {
            if (position.X < box.X1 * width || position.X > box.X2 * width
                || position.Y < box.Y1 * height || position.Y > box.Y2 * height)
            {
                continue;
            }

            ShowTooltip(box, position, width, height);
            return;
        }

        HideTooltip();
    }

    private void ShowTooltip(DetectionBoxViewModel box, Point position, double width, double height)
    {
        if (_tooltip is null || _tooltipText is null)
        {
            return;
        }

        _tooltipText.Text = box.Label;
        _tooltip.Measure(Size.Infinity);
        var size = _tooltip.DesiredSize;

        // 靠近右/下边缘时往回收，避免标签被裁掉
        var left = Math.Clamp(position.X + 10, 0, Math.Max(0, width - size.Width));
        var top = Math.Clamp(position.Y + 14, 0, Math.Max(0, height - size.Height));

        Canvas.SetLeft(_tooltip, left);
        Canvas.SetTop(_tooltip, top);
        _tooltip.IsVisible = true;
    }

    private void HideTooltip()
    {
        if (_tooltip is not null)
        {
            _tooltip.IsVisible = false;
        }
    }
}
