namespace Bold.UpgradeCenter.Services;

public interface IUpgradeRollbackJobRunner
{
    void Start(string rollbackJobId, string rollbackId);

    void Recover(string rollbackJobId);
}
