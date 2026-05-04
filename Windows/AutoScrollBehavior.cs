using System;
using System.Windows;
using System.Windows.Controls;

namespace DeepSeek_v4_for_VisualStudio.Windows
{
    public static class AutoScrollBehavior
    {
        public static readonly DependencyProperty AutoScrollProperty =
            DependencyProperty.RegisterAttached(
                "AutoScroll",
                typeof(bool),
                typeof(AutoScrollBehavior),
                new PropertyMetadata(false, AutoScrollPropertyChanged));

        public static void SetAutoScroll(DependencyObject obj, bool value)
        {
            obj.SetValue(AutoScrollProperty, value);
        }

        public static bool GetAutoScroll(DependencyObject obj)
        {
            return (bool)obj.GetValue(AutoScrollProperty);
        }

        private static void AutoScrollPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer scrollViewer)
            {
                if ((bool)e.NewValue)
                {
                    scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;

                    // 如果 ScrollViewer 已加载完成，立即滚动到底部；
                    // 否则等待 Loaded 事件（处理初始加载历史消息的场景）
                    if (scrollViewer.IsLoaded)
                    {
                        scrollViewer.ScrollToBottom();
                    }
                    else
                    {
                        scrollViewer.Loaded += OnScrollViewerLoaded;
                    }
                }
                else
                {
                    scrollViewer.ScrollChanged -= ScrollViewer_ScrollChanged;
                    scrollViewer.Loaded -= OnScrollViewerLoaded;
                }
            }
        }

        private static void OnScrollViewerLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ScrollViewer scrollViewer)
            {
                scrollViewer.Loaded -= OnScrollViewerLoaded;
                // 延迟到下一个布局 pass，确保 ItemsControl 已完成布局测量
                scrollViewer.Dispatcher.BeginInvoke(
                    new Action(() => scrollViewer.ScrollToBottom()),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private static void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // 当内部内容高度增加（比如 AI 正在流式输出或发送新消息时），自动滚动到底部
            if (sender is ScrollViewer scrollViewer && e.ExtentHeightChange > 0)
            {
                scrollViewer.ScrollToBottom();
            }
        }
    }
}