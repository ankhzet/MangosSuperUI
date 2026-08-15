namespace MangosSuperUI.Services;

public class VmangosSettings
{
    public string BinDirectory { get; set; } = "";
    public string LogDirectory { get; set; } = "";
    public string ConfigDirectory { get; set; } = "";
    public string MangosdProcess { get; set; } = "mangosd";
    public string RealmdProcess { get; set; } = "realmd";
    public string MangosdConfPath { get; set; } = "";
    public string LogsDir { get; set; } = "";
    public string DbcPath { get; set; } = "/home/wowvmangos/vmangos/run/data/5875/dbc";
    public string MapsDataPath { get; set; } = "/home/wowvmangos/vmangos/run/data/maps";
    public string BackupDirectory { get; set; } = "/home/wowvmangos/backups";
    public string VmangosSourcePath { get; set; } = "/home/wowvmangos/vmangos/src";
    public string VmangosSqlPath { get; set; } = "/home/wowvmangos/vmangos/sql";

    // Commands used by ProcessManagerService to control the world/auth
    // servers. Default is the classic systemd-via-sudo invocation; in a
    // container/Docker setup leave them blank and the service falls back
    // to signaling the process directly via Process.Kill (UI shares the
    // mangosd container's PID namespace via pid: "service:mangosd").
    //
    // {unit} is replaced with the value of MangosdProcess / RealmdProcess.
    // {action} is replaced with start | stop | restart.
    public string MangosdStartCommand   { get; set; } = "sudo systemctl start {unit}";
    public string MangosdStopCommand    { get; set; } = "sudo systemctl stop {unit}";
    public string MangosdRestartCommand { get; set; } = "sudo systemctl restart {unit}";
    public string RealmdStartCommand   { get; set; } = "sudo systemctl start {unit}";
    public string RealmdStopCommand    { get; set; } = "sudo systemctl stop {unit}";
    public string RealmdRestartCommand { get; set; } = "sudo systemctl restart {unit}";
}

public class RemoteAccessSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3443;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int ReconnectDelayMs { get; set; } = 3000;
    public int CommandTimeoutMs { get; set; } = 5000;
}