using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UnityWaxKit
{
    /// <summary>Reads the media references of an asset or template with the field names of the settings.</summary>
    internal static class WaxNftMediaData
    {
        public static WaxNftMediaInfo FromAsset(WaxMediaSettings settings, WaxAtomicAsset asset)
        {
            // Data has the template, immutable and mutable data merged; the template alone when Data is missing.
            var data = asset.Data ?? Merge(asset.Template?.ImmutableData, asset.ImmutableData, asset.MutableData);
            var info = Read(settings, data, asset.Collection?.CollectionName, asset.Schema?.SchemaName);
            info.AssetId = asset.AssetId;
            info.TemplateId = asset.Template?.TemplateId;
            return info;
        }

        public static WaxNftMediaInfo FromTemplate(WaxMediaSettings settings, WaxAtomicTemplate template)
        {
            var info = Read(settings, template.ImmutableData, template.Collection?.CollectionName, template.Schema?.SchemaName);
            info.TemplateId = template.TemplateId;
            return info;
        }

        /// <summary>
        /// A template by id: with the collection the single-template call, without it the list call filtered by id
        /// (templates?ids=). Null when there is none.
        /// </summary>
        public static async Task<WaxAtomicTemplate> GetTemplate(WaxAtomicAssets atomic, string templateId, string collection)
        {
            if (!string.IsNullOrEmpty(collection)) return await atomic.GetTemplate(collection, templateId);
            var list = await atomic.GetTemplates(new WaxAtomicQuery { Ids = templateId, Limit = 1 });
            return list.FirstOrDefault(t => t.TemplateId == templateId);
        }

        internal static WaxNftMediaInfo Read(WaxMediaSettings settings, JObject data, string collection, string schema)
        {
            var (image, video, cover) = settings.FieldsFor(collection, schema);
            return new WaxNftMediaInfo
            {
                Collection = collection,
                Schema = schema,
                Name = Text(data, "name"),
                Image = Text(data, image),
                Video = Text(data, video),
                Cover = Text(data, cover),
            };
        }

        static string Text(JObject data, string field)
        {
            var token = data?[field];
            if (token == null || token.Type != JTokenType.String) return null;
            var text = ((string)token).Trim();
            return text.Length == 0 ? null : text;
        }

        static JObject Merge(params JObject[] parts)
        {
            var merged = new JObject();
            foreach (var part in parts.Where(p => p != null))
                foreach (var property in part.Properties())
                    merged[property.Name] = property.Value;
            return merged;
        }

        /// <summary>The references to show, in order, for a mode: the video (animated) or the cover / image (static).</summary>
        internal static IEnumerable<string> StillReferences(WaxNftMediaInfo info)
        {
            if (info.HasVideo && info.Cover != null && info.Cover != info.Video) yield return info.Cover;
            if (info.Image != null && info.Image != info.Video && info.Image != info.Cover) yield return info.Image;
        }
    }
}
