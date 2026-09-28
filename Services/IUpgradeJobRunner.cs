namespace Bold.UpgradeCenter.Services;

public interface IUpgradeJobRunner
{
    void Start(string jobId);

    void Recover(string jobId);
}
