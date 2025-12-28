
using System.Collections.ObjectModel;
using CUE4Parse.FileProvider.Objects;

namespace FModel.ViewModels
{
    public class ReferenceViewerViewModel
    {
        public ObservableCollection<ReferenceNodeViewModel> References { get; set; } = new();

        public ReferenceViewerViewModel(ReferenceNodeViewModel root)
        {
            References.Add(root);
        }

        // GameFileから直接初期化できるコンストラクタを追加
        public ReferenceViewerViewModel(GameFile file)
        {
            // 仮実装: ファイル名をルートノードに
            var root = new ReferenceNodeViewModel
            {
                DisplayName = file.Name,
                AssetPathName = file.Path
            };
            // TODO: fileの内容からChildrenを構築する処理を追加
            References.Add(root);
        }
    }

    public class ReferenceNodeViewModel
    {
        public string DisplayName { get; set; }
        public string AssetPathName { get; set; }
        public ObservableCollection<ReferenceNodeViewModel> Children { get; set; } = new();
    }
}
