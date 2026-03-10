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
        // single-file 앱에서는 AppContext.BaseDirectory가 임시 폴더를 가리킬 수 있음
        var exeDir = (Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        _logger.LogInformation("Executable directory: {Dir}", exeDir);

        var driveRoot = Path.GetPathRoot(exeDir);
        if (driveRoot != null)
        {
            try
            {
                var driveInfo = new DriveInfo(driveRoot);

                // 이동식/네트워크 드라이브면 드라이브 루트 사용
                if (driveInfo.DriveType == DriveType.Removable ||
                    driveInfo.DriveType == DriveType.Network)
                {
                    _logger.LogInformation("Detected removable/network drive root: {Root}", driveRoot);
                    return driveRoot;
                }

                // 시스템 드라이브가 아닌 고정 드라이브 (외장 HDD는 Fixed로 잡히는 경우가 많음)
                // C:\, 시스템 드라이브를 제외한 다른 드라이브면 드라이브 루트 사용
                if (driveInfo.DriveType == DriveType.Fixed && !IsSystemDrive(driveRoot))
                {
                    _logger.LogInformation("Detected non-system fixed drive root: {Root}", driveRoot);
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
        if (parent != null)
        {
            _logger.LogInformation("Using parent directory as watch root: {Root}", parent.FullName);
            return parent.FullName;
        }

        // 4. 최종 폴백: 실행 파일 디렉토리 자체
        _logger.LogInformation("Using executable directory as watch root: {Root}", exeDir);
        return exeDir;
    }

    private static bool IsSystemDrive(string driveRoot)
    {
        try
        {
            // Windows: 환경 변수로 시스템 드라이브 확인
            var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(systemRoot))
            {
                var systemDrive = Path.GetPathRoot(systemRoot);
                return string.Equals(driveRoot, systemDrive, StringComparison.OrdinalIgnoreCase);
            }

            // Linux/Mac: / 루트는 시스템 드라이브
            if (driveRoot == "/") return true;
        }
        catch { }

        return false;
    }

    public string DetectStorageName(string watchRoot)
    {
        try
        {
            var driveRoot = Path.GetPathRoot(watchRoot);
            if (driveRoot != null)
            {
                var driveInfo = new DriveInfo(driveRoot);
                if (driveInfo.IsReady && !string.IsNullOrWhiteSpace(driveInfo.VolumeLabel))
                    return $"{driveInfo.VolumeLabel} ({driveRoot.TrimEnd(Path.DirectorySeparatorChar)})";

                return $"{driveInfo.DriveType} ({driveRoot.TrimEnd(Path.DirectorySeparatorChar)})";
            }
        }
        catch
        {
            // DriveInfo 접근 실패 시 (Linux 등)
        }

        // 폴백: 폴더 이름 사용
        return Path.GetFileName(watchRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? watchRoot;
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
