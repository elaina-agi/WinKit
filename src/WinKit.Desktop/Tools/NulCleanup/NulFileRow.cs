using WinKit.Core.NulCleanup;
using WinKit.Desktop.Infrastructure;

namespace WinKit.Desktop.Tools.NulCleanup;

public sealed class NulFileRow(NulFileCandidate candidate) : ObservableObject
{
    private bool _isSelected;
    private string _status = "待处理";
    private string _message = "扫描发现的普通 NUL 文件；删除前会重新验证。";
    private bool _canSelect = true;
    public NulFileCandidate Candidate { get; } = candidate;
    public string FullPath => Candidate.FullPath;
    public string SizeText => FileSizeFormatter.Format(Candidate.Size);
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value && CanSelect);
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public bool CanSelect { get => _canSelect; private set => SetProperty(ref _canSelect, value); }

    public void RequireRescan(string message)
    {
        Status = "需重新扫描";
        Message = message;
        CanSelect = false;
        IsSelected = false;
    }

    public void Apply(DeleteResult result)
    {
        Status = result.Status switch
        {
            DeleteStatus.Deleted => "已删除",
            DeleteStatus.AlreadyMissing => "已不存在",
            DeleteStatus.Refused => "已拒绝",
            _ => "失败"
        };
        Message = result.Message;
        // Every attempt clears the selection. Rejected identities require a new scan.
        CanSelect = result.Status == DeleteStatus.Failed;
        IsSelected = false;
    }
}
