using Arctis.Imaging;
using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.EngineWork;

namespace ArctisAurora.Tests
{
    internal static class ImageDecodePerfTests
    {
        private const int decodes = 64;

        // one decodable file per frame, the zone it is timed under, and its decoded RGBA8 size
        private struct Work
        {
            public byte[][] files;
            public string[] zones;
            public int largest;
        }

        [A_XSDActionDependency("Perf.ImageDecode.Atlases.Arctis", "Test")]
        private static IEnumerator<int> AtlasesArctis(TestContext t) => Measure(t, Atlases("Image.Decode.Arctis.Atlas", true));

        [A_XSDActionDependency("Perf.ImageDecode.Atlases.Adf", "Test")]
        private static IEnumerator<int> AtlasesAdf(TestContext t) => Measure(t, Atlases("Image.Decode.Arctis.AtlasAdf", false));

        // Decodes one file per frame into a reused RGBA8 buffer, each under its zone.
        private static IEnumerator<int> Measure(TestContext t, Func<Work> prepare)
        {
            if (Engine.isDebug || !Profiling.compiledIn)
            {
                t.StartMeasure();
                yield return t.EndMeasure();
                yield break;
            }

            Work work = prepare();
            byte[] pixels = new byte[work.largest];
            DecodeOptions options = new DecodeOptions(PixelLayout.RGBA8);
            byte[][] files = work.files;
            string[] zones = work.zones;

            t.StartMeasure();
            yield return 1;
            for (int i = 0; i < files.Length; i++)
            {
                Profiling.Zone.Start(zones[i]);
                ImageCodec.Decode(files[i], pixels, options);
                Profiling.Zone.End(zones[i]);
                yield return 1;
            }
            yield return t.EndMeasure();
        }

        // The font and icon atlases as baked, or re-encoded as PNG, cycled into 64 separate copies.
        private static Func<Work> Atlases(string zone, bool png) => () =>
        {
            string[] fonts = Directory.GetFiles(VirtualFileSystem.ResolveDir("Fonts"), "*_atlas.adf", SearchOption.AllDirectories);
            byte[][] sources = new byte[fonts.Length + 1][];
            for (int i = 0; i < fonts.Length; i++)
                sources[i] = File.ReadAllBytes(fonts[i]);
            sources[fonts.Length] = File.ReadAllBytes(VirtualFileSystem.ResolveFile("Icons/default/default_atlas.adf"));
            if (png)
                for (int i = 0; i < sources.Length; i++)
                {
                    Arctis.Imaging.Image atlas = Arctis.Imaging.Image.Load(sources[i]);
                    using MemoryStream stream = new MemoryStream();
                    atlas.SavePng(stream);
                    sources[i] = stream.ToArray();
                }

            Work work = new Work { files = new byte[decodes][], zones = new string[decodes] };
            for (int i = 0; i < decodes; i++)
            {
                work.files[i] = (byte[])sources[i % sources.Length].Clone();
                work.zones[i] = zone;
            }
            work.largest = Largest(sources);
            return work;
        };

        private static int Largest(byte[][] files)
        {
            int largest = 0;
            for (int i = 0; i < files.Length; i++)
            {
                ImageCodec.TryIdentify(files[i], out ImageHeader header);
                largest = Math.Max(largest, header.Width * header.Height * 4);
            }
            return largest;
        }
    }
}
