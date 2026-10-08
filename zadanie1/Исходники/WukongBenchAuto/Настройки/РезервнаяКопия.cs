namespace WukongBenchAuto.Settings;

// Бэкап GameUserSettings.ini на время тестов.
// Копия лежит рядом с ini: если прогу убьют посреди теста, при следующем запуске она найдёт копию и вернёт настройки.
internal sealed class SettingsSession : IDisposable
{
    private readonly string _iniPath;
    private readonly string _backupPath;
    private readonly string _absentMarkerPath;
    private readonly bool _restoreOnDispose;
    private bool _restored;

    public SettingsSession(string iniPath, bool restoreOnDispose)
    {
        _iniPath = iniPath;
        _backupPath = iniPath + ".wukongbenchauto.bak";
        _absentMarkerPath = iniPath + ".wukongbenchauto.absent";
        _restoreOnDispose = restoreOnDispose;

        RecoverStaleBackup();
        Directory.CreateDirectory(Path.GetDirectoryName(iniPath)!);
        if (File.Exists(iniPath)) File.Copy(iniPath, _backupPath, overwrite: true);
        else File.WriteAllText(_absentMarkerPath, "");
    }

    public void Apply(BenchmarkProfile profile)
    {
        var ini = File.Exists(_iniPath) ? IniFile.Load(_iniPath) : IniFile.Empty();
        SettingsWriter.Apply(ini, profile);
        ini.Save(_iniPath);
    }

    public void Restore()
    {
        if (_restored) return;
        _restored = true;

        if (File.Exists(_backupPath))
        {
            if (File.Exists(_iniPath)) File.SetAttributes(_iniPath, FileAttributes.Normal);
            File.Copy(_backupPath, _iniPath, overwrite: true);
            File.Delete(_backupPath);
        }
        else if (File.Exists(_absentMarkerPath))
        {
            if (File.Exists(_iniPath)) File.Delete(_iniPath);
            File.Delete(_absentMarkerPath);
        }
    }

    // для --keep-settings: настройки тестов оставляем, бэкап просто удаляем
    public void Discard()
    {
        _restored = true;
        if (File.Exists(_backupPath)) File.Delete(_backupPath);
        if (File.Exists(_absentMarkerPath)) File.Delete(_absentMarkerPath);
    }

    public void Dispose()
    {
        if (_restoreOnDispose) Restore();
        else Discard();
    }

    private void RecoverStaleBackup()
    {
        if (!File.Exists(_backupPath) && !File.Exists(_absentMarkerPath)) return;
        Log.Warn("Найдена резервная копия настроек от прерванного запуска — восстанавливаю исходный GameUserSettings.ini.");
        Restore();
        _restored = false;
    }
}
