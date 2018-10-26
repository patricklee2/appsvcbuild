﻿using System.Collections.Generic;
using Newtonsoft.Json;

namespace appsvcbuild
{
    public class TagList
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("next")]
        public string Next { get; set; }

        [JsonProperty("previous")]
        public string Previous { get; set; }

        [JsonProperty("results")]
        public List<Tag> Results { get; set; }
    }
}
