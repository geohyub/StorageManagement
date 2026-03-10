namespace StorageAudit.Services;

using StorageAudit.Models;

public class StorageRootDetector
{
    private readonly ILogger<StorageRootDetector> _logger;

    public StorageRootDetector(ILogger<StorageRootDetector> logger)
    {
        _logger = logger;
    }

    public string DetectRoot(AuditConfig config)
    {
        // 1. config에 명시적 WatchRoot가 있으면 사용
        if (!string.IsNullOrEmpty(config.WatchRoot) && Directory.Exists(config.WatchRoot))
        {
            _logger.LogInformation("Using configured watch root: {Root}", config.WatchRoot);
            return Path.GetFullPath(config.WatchRoot);
        }

        // 2. 실행 파일 위치에서 저장소 루트 추론
        var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        _logger.LogInformation("Executable directory: {Dir}", exeDir);

        // 드라이브 루트인지 확인 (USB 드라이브 등은 보통 드라이브 루트)
        var driveRoot = Path.GetPathRoot(exeDir);
        if (driveRoot != null)
        {
            // 실행 위치가 C:\ 같은 시스템 드라이브가 아닌 이동식 드라이브면 드라이브 루트 사용
            try
            {
                var driveInfo = new DriveInfo(driveRoot);
                if (driveInfo.DriveType == DriveType.Removable ||
                    driveInfo.DriveType == DriveType.Network)
                {
                    _logger.LogInformation("Detected removable/network drive root: {Root}", driveRoot);
                    return driveRoot;
                }
            }
            catch
            {
                // DriveInfo 접근 실패 시 무시 (Linux 등)
            }
        }

        // 3. 실행 파일의 상위 폴더를 감시 루트로 사용
        // (exe가 StorageAudit/ 폴더 안에 있다면 한 단계 위가 저장소 루트)
        var parent = Directory.GetParent(exeDir);
        if (parent != null && parent.FullName != driveRoot)
        {
            _logger.LogInformation("Using parent directory as watch root: {Root}", parent.FullName);
            return parent.FullName;
        }

        // 4. 최종 폴백: 실행 파일 디렉토리 자체
        _logger.LogInformation("Using executable directory as watch root: {Root}", exeDir);
        return exeDir;
    }

    public void EnsureSystemFolders(string watchRoot, AuditConfig config)
    {
        var sysFolder = config.GetSystemFolder(watchRoot);
        Directory.CreateDirectory(sysFolder);
        Directory.CreateDirectory(Path.Combine(sysFolder, "logs"));
        Directory.CreateDirectory(config.GetExportFolder(watchRoot));

        _logger.LogInformation("System folders ensured at: {Folder}", sysFolder);
    }
}
