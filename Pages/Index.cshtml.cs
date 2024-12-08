using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace GeminiFreeSearch.Pages
{
    public class IndexModel : PageModel
    {
        private readonly IConfiguration _configuration;

        public List<SelectListItem> AvailableModels { get; private set; } = new();

        public IndexModel(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public void OnGet()
        {
            AvailableModels = _configuration.GetSection("GeminiApi:Models")
                .GetChildren()
                .Select(x => new SelectListItem(x.Key, x.Key))
                .OrderBy(x => x.Text)
                .ToList();
        }
    }
}
