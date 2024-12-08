using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;

namespace GeminiFreeSearch.Pages
{
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [IgnoreAntiforgeryToken]
    public class ErrorModel : PageModel
    {
        public string? RequestId { get; set; }

        public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

        private readonly ILogger<ErrorModel> _logger;
        private readonly IWebHostEnvironment _environment;

        public ErrorModel(ILogger<ErrorModel> logger, IWebHostEnvironment environment)
        {
            _logger = logger;
            _environment = environment;
        }

        public bool IsDevMode => _environment.IsDevelopment();

        public void OnGet()
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
            _logger.LogError($"Error occurred. RequestId: {RequestId}");
        }
    }

}
