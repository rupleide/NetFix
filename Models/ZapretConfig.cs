using System.Collections.Generic;

namespace NetFix.Models;

public class ZapretConfig
{
    public string Name { get; set; } = "";
    public Dictionary<string, ServiceTestResult> Tests { get; set; } = new();
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public int AveragePing { get; set; }
    public bool IsValid { get; set; }
}

public class ServiceTestResult
{
    public string ServiceName { get; set; } = "";
    public string HttpStatus { get; set; } = "";
    public string Tls12Status { get; set; } = "";
    public string Tls13Status { get; set; } = "";
    public int Ping { get; set; }
    
    public bool IsSuccess => 
        HttpStatus == "OK" && 
        Tls12Status == "OK" && 
        Tls13Status == "OK";
}

public class ZapretConfigCache
{
    public string LastTested { get; set; } = "";
    public string CurrentConfig { get; set; } = "";
    public List<ZapretConfig> ValidConfigs { get; set; } = new();
}
