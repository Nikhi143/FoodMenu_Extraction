using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FoodMenu_Extract.Models
{
    public class HotelDto
    {
        [JsonPropertyName("productID")]
        public string ProductId { get; set; }

        [JsonPropertyName("hotelCode")]
        public string HotelCode { get; set; }

        [JsonPropertyName("destCode")]
        public string DestinationCode { get; set; }

        [JsonPropertyName("address")]
        public string Address { get; set; }

        [JsonPropertyName("hotelName")]
        public string HotelName { get; set; }

        // This will hold the complete raw JSON string for the CompleteJson column
        [JsonIgnore]
        public string FullJson { get; set; }

        public static HotelDto FromJson(string json, JsonSerializerOptions options)
        {
            var dto = JsonSerializer.Deserialize<HotelDto>(json, options);
            // Store the raw JSON string back into the DTO for the DB column
            if (dto != null)
            {
                dto.FullJson = json;
            }
            return dto;
        }
    }
}
