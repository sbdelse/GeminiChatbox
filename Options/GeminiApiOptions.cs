using System.ComponentModel.DataAnnotations;

public class GeminiApiOptions
{
    [Required]
    public string[] ApiKeys { get; set; } = Array.Empty<string>();
    
    public string[] PremiumApiKeys { get; set; } = Array.Empty<string>();
    
    [Required]
    [Url]
    public string BaseUrl { get; set; } = string.Empty;
    
    [Required]
    public Dictionary<string, ModelConfig> Models { get; set; } = new();

    public class ModelConfig
    {
        public int RPM { get; set; }
        public int TPM { get; set; }
        public int RPD { get; set; }
        public string? FallbackModel { get; set; }
    }
} 