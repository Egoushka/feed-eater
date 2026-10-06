using System.Xml;
using System.Xml.Linq;

namespace FeedEater.Sources;

/// <summary>Reads untrusted XML: no DTD, no resolver, and no tree deeper than <see cref="MaxDepth"/>.</summary>
internal static class SafeXml
{
    /// <summary>Real feeds and OPML files stay under 15; <c>XElement.Value</c> and the OPML walk recurse, so a deeper document would overflow the stack.</summary>
    public const int MaxDepth = 64;

    /// <summary>The root element, or null when there is none. Throws <see cref="XmlException"/> for malformed or too deeply nested XML.</summary>
    public static XElement? Root(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, CheckCharacters = false };
        using (var scan = XmlReader.Create(new StringReader(xml), settings))
        {
            while (scan.Read())
            {
                if (scan.Depth > MaxDepth)
                {
                    throw new XmlException($"The document is nested deeper than {MaxDepth} levels.");
                }
            }
        }

        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader).Root;
    }
}
