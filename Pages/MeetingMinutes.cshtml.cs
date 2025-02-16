using Microsoft.AspNetCore.Mvc.RazorPages;
using GeminiFreeSearch.Services;

namespace GeminiFreeSearch.Pages
{
    public class MeetingMinutesModel : PageModel
    {
        private readonly MeetingMinutesService _meetingMinutesService;

        public MeetingMinutesModel(MeetingMinutesService meetingMinutesService)
        {
            _meetingMinutesService = meetingMinutesService;
        }

        public void OnGet()
        {
        }
    }
}