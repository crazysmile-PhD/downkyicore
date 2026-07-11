using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DownKyi.Models
{
    public class VideoPlayUrlBasic
    {
        public IReadOnlyList<string> BackupUrl { get; set; } = Array.Empty<string>();
        public string BaseUrl { get; set; } = string.Empty;

        public int Id { get; set; }

        public string Codecs { get; set; } = string.Empty;

        public long ExpectedSize { get; set; }
    }
}
