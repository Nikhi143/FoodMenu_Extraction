using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FoodMenu_Extract.Models
{
    public class MenuResultDto
    {
        public string HotelProductId { get; set; } = string.Empty;
        public string HotelName { get; set; } = string.Empty;
        public string HotelAddress { get; set; } = string.Empty;
        public bool HasMenu { get; set; }
        public string? MenuSourceUrl { get; set; }
        public string? MenuText { get; set; }

        // When a structured menu is found, this property will contain serialized JSON (StructuredMenu)
        [JsonPropertyName("menuJson")]
        public string? MenuJson { get; set; }
    }
}
