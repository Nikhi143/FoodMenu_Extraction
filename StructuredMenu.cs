using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FoodMenu_Extract.Models
{
    public class StructuredMenu
    {
        [JsonPropertyName("sourceUrl")]
        public string? SourceUrl { get; set; }

        [JsonPropertyName("sections")]
        public List<MenuSection> Sections { get; set; } = new List<MenuSection>();
    }

    public class MenuSection
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("items")]
        public List<MenuItem> Items { get; set; } = new List<MenuItem>();
    }

    public class MenuItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        // price as string to preserve currency formatting; parse to decimal separately if needed
        [JsonPropertyName("price")]
        public string? Price { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
