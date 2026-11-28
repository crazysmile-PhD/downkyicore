using System;
using System.Collections.Generic;
using System.Threading;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Presentation;
using DownKyi.Utils;
using DownKyi.ViewModels.Toolbox;

namespace DownKyi.ViewModels
{
    internal class ToolboxViewModel : ViewModelBase
    {
        public const string Tag = "PageToolbox";
        private CancellationTokenSource? _visibleCancellation;

        #region 页面属性申明

        private IReadOnlyList<TabHeader> _tabHeaders = Array.Empty<TabHeader>();

        public IReadOnlyList<TabHeader> TabHeaders
        {
            get => _tabHeaders;
            set => SetProperty(ref _tabHeaders, value);
        }

        private int _selectTabId;

        public int SelectTabId
        {
            get => _selectTabId;
            set => SetProperty(ref _selectTabId, value);
        }

        #endregion

        public ToolboxViewModel(IDesktopInteractionContext desktopInteractions)
            : base(desktopInteractions)
        {
            ObserveRegion(AppNavigationRegion.Toolbox);
            #region 属性初始化

            TabHeaders = new List<TabHeader>
            {
                new() { Id = 0, Title = DictionaryResource.GetString("BiliHelper") },
                new() { Id = 1, Title = DictionaryResource.GetString("Delogo") },
                new() { Id = 2, Title = DictionaryResource.GetString("ExtractMedia") }
            };

            #endregion
        }

        #region 命令申明

        // 返回事件
        private RelayCommand? _backSpaceCommand;

        public RelayCommand BackSpaceCommand => _backSpaceCommand ??= new RelayCommand(ExecuteBackSpace);

        /// <summary>
        /// 返回事件
        /// </summary>
        protected internal override void ExecuteBackSpace()
        {
            if (TryNavigateBack())
            {
                return;
            }

            NavigateToParent();
        }

        // 左侧tab点击事件
        private RelayCommand<object>? _leftTabHeadersCommand;

        public RelayCommand<object> LeftTabHeadersCommand => _leftTabHeadersCommand ??= RequiredParameterCommand.Create<object>(ExecuteLeftTabHeadersCommand);

        /// <summary>
        /// 左侧tab点击事件
        /// </summary>
        /// <param name="parameter"></param>
        private void ExecuteLeftTabHeadersCommand(object parameter)
        {
            if (parameter is not TabHeader tabHeader || _visibleCancellation == null)
            {
                return;
            }

            var lifetime = _visibleCancellation.Token;
            switch (tabHeader.Id)
            {
                case 0:
                    NavigateTool(AppRoute.BiliHelper, lifetime);
                    break;
                case 1:
                    NavigateTool(AppRoute.Delogo, lifetime);
                    break;
                case 2:
                    NavigateTool(AppRoute.ExtractMedia, lifetime);
                    break;
            }
        }

        #endregion

        /// <summary>
        /// 导航到页面时执行
        /// </summary>
        /// <param name="navigationContext"></param>
        public override void OnNavigatedTo(AppNavigationContext navigationContext)
        {
            base.OnNavigatedTo(navigationContext);
            var lifetime = ReplaceCancellationSource(ref _visibleCancellation);

            // 进入设置页面时显示的设置项
            SelectTabId = 0;
            PropertyChangeAsync(() =>
                NavigateTool(AppRoute.BiliHelper, lifetime));
        }

        private void NavigateTool(AppRoute route, CancellationToken lifetime)
        {
            if (lifetime.IsCancellationRequested) return;
            Navigation.NavigateRegion(AppNavigationRegion.Toolbox, route,
                new Dictionary<string, object?> { ["lifetime"] = lifetime });
        }

        public override void OnNavigatedFrom(AppNavigationContext navigationContext)
        {
            CancelAndDispose(ref _visibleCancellation);
            base.OnNavigatedFrom(navigationContext);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed) CancelAndDispose(ref _visibleCancellation);
            base.Dispose(disposing);
        }
    }
}
