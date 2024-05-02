using System;

namespace Flower.Models
{
    public record Track
    {
        public string Title { get; set; }
        public string Artists { get; set; }
        public string Album { get; set; }
        public string Genre { get; set; }
        public string Year { get; set; }
        public TimeSpan Duration { get; set; }
        public string Path { get; set; }
    }
}
