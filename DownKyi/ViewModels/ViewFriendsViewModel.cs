using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Images;
using DownKyi.Utils;
using DownKyi.ViewModels.Friends;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.ViewModels
{
    internal class ViewFriendsViewModel : ViewModelBase
    {
        public const string Tag = "PageFriends";

        private long mid = -1;

        #region 页面属性申明

        private VectorImage _arrowBack = null!;

        public VectorImage ArrowBack
        {
            get => _arrowBack;
            set => SetProperty(ref _arrowBack, value);
        }

        private ObservableCollection<TabHeader> _tabHeaders = new();

        public ObservableCollection<TabHeader> TabHeaders
        {
            get => _tabHeaders;
            private set => SetProperty(ref _tabHeaders, value);
        }

        private int _selectTabId = -1;

        public int SelectTabId
        {
            get => _selectTabId;
            set => SetProperty(ref _selectTabId, value);
        }

        #endregion

        public ViewFriendsViewModel(IDesktopInteractionContext desktopInteractions)
            : base(desktopInteractions)
        {
            ObserveRegion(AppNavigationRegion.Friends);
            #region 属性初始化

            ArrowBack = NavigationIcon.CreateArrowBack();
            ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

            TabHeaders = new ObservableCollection<TabHeader>
            {
                new() { Id = 0, Title = DictionaryResource.GetString("FriendFollowing") },
                new() { Id = 1, Title = DictionaryResource.GetString("FriendFollower") },
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
            //InitView();

            ArrowBack.Fill = DictionaryResource.GetColor("ColorText");

            if (TryNavigateBack())
            {
                return;
            }

            NavigateToParent();
        }

        // 顶部tab点击事件
        private RelayCommand<object>? _tabHeadersCommand;

        public RelayCommand<object> TabHeadersCommand => _tabHeadersCommand ??= RequiredParameterCommand.Create<object>(ExecuteTabHeadersCommand);

        /// <summary>
        /// 顶部tab点击事件
        /// </summary>
        /// <param name="parameter"></param>
        private void ExecuteTabHeadersCommand(object parameter)
        {
            if (parameter is not TabHeader tabHeader)
            {
                return;
            }

            // TODO
            // 此处应该根据具体状态传入true or false
            NavigationView(tabHeader.Id, true);
        }

        #endregion

        /// <summary>
        /// 进入子页面
        /// </summary>
        /// <param name="id"></param>
        /// <param name="isFirst"></param>
        private void NavigationView(long id, bool isFirst)
        {
            // isFirst参数表示是否是从PageFriends的headerTable的item点击进入的
            // true表示加载PageFriends后第一次进入
            // false表示从headerTable的item点击进入
            var parameters = new Dictionary<string, object?>
            {
                ["mid"] = mid,
                ["isFirst"] = isFirst
            };

            switch (id)
            {
                case 0:
                    Navigation.NavigateRegion(
                        AppNavigationRegion.Friends,
                        AppRoute.Following,
                        parameters);
                    break;
                case 1:
                    Navigation.NavigateRegion(
                        AppNavigationRegion.Friends,
                        AppRoute.Follower,
                        parameters);
                    break;
            }
        }

        /// <summary>
        /// 导航到页面时执行
        /// </summary>
        /// <param name="navigationContext"></param>
        public override void OnNavigatedTo(AppNavigationContext navigationContext)
        {
            ArgumentNullException.ThrowIfNull(navigationContext);
            base.OnNavigatedTo(navigationContext);

            ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

            // 根据传入参数不同执行不同任务
            var parameter = navigationContext.Parameters.GetValue<Dictionary<string, object>>("Parameter");
            if (parameter == null)
            {
                return;
            }

            mid = (long)parameter["mid"];
            SelectTabId = (int)parameter["friendId"];

            PropertyChangeAsync(() =>
            {
                NavigationView(SelectTabId, true);
            });
        }
    }
}
