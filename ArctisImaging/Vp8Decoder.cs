using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Arctis.Imaging
{
    // VP8 key frames as WebP stores them: boolean-coded modes and coefficients, intra prediction, the inverse DCT and WHT,
    // the simple or normal loop filter, then libwebp's fancy 4:2:0 upsampling into RGBA.
    internal static class Vp8Decoder
    {
        // prediction modes; 16x16 and chroma use the first four, and the last three are DC at the frame's edges
        private const int DcPred = 0, TmPred = 1, VePred = 2, HePred = 3, RdPred = 4, VrPred = 5, LdPred = 6, VlPred = 7, HdPred = 8, HuPred = 9;
        private const int DcNoTop = 10, DcNoLeft = 11, DcNoTopLeft = 12;
        private const int TypeI16Ac = 0, TypeY2 = 1, TypeChroma = 2, TypeI4 = 3;
        private const int ProbaCount = 4 * 8 * 3 * 11;

        private static ReadOnlySpan<byte> DcTable => new byte[]
        {
            4, 5, 6, 7, 8, 9, 10, 10, 11, 12, 13, 14, 15, 16, 17, 17, 18, 19, 20, 20, 21, 21, 22, 22, 23, 23, 24, 25, 25, 26, 27, 28,
            29, 30, 31, 32, 33, 34, 35, 36, 37, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58,
            59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89,
            91, 93, 95, 96, 98, 100, 101, 102, 104, 106, 108, 110, 112, 114, 116, 118, 122, 124, 126, 128, 130, 132, 134, 136, 138, 140, 143, 145, 148, 151, 154, 157
        };

        private static ReadOnlySpan<ushort> AcTable => new ushort[]
        {
            4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35,
            36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 60, 62, 64, 66, 68, 70, 72, 74, 76,
            78, 80, 82, 84, 86, 88, 90, 92, 94, 96, 98, 100, 102, 104, 106, 108, 110, 112, 114, 116, 119, 122, 125, 128, 131, 134, 137, 140, 143, 146, 149, 152,
            155, 158, 161, 164, 167, 170, 173, 177, 181, 185, 189, 193, 197, 201, 205, 209, 213, 217, 221, 225, 229, 234, 239, 245, 249, 254, 259, 264, 269, 274, 279, 284
        };

        private static ReadOnlySpan<byte> Zigzag => new byte[] { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };

        // the band of each coefficient position, with a sentinel for the position past the last
        private static ReadOnlySpan<byte> Bands => new byte[] { 0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0 };

        // extra-bit probabilities of the DCT_CAT3..6 tokens, one run each
        private static ReadOnlySpan<byte> CatProbas => new byte[] { 173, 148, 140, 176, 155, 140, 135, 180, 157, 141, 134, 130, 254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129 };
        private static ReadOnlySpan<byte> CatStarts => new byte[] { 0, 3, 7, 12, 23 };

        // the 4x4 mode tree: a positive entry is the next node, otherwise the negated mode
        private static ReadOnlySpan<sbyte> ModeTree => new sbyte[] { -DcPred, 1, -TmPred, 2, -VePred, 3, 4, 6, -HePred, 5, -RdPred, -VrPred, -LdPred, 7, -VlPred, 8, -HdPred, -HuPred };

        // default coefficient probabilities [type][band][context][11]
        private static ReadOnlySpan<byte> CoeffsProba0 => new byte[]
        {
            128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
            253, 136, 254, 255, 228, 219, 128, 128, 128, 128, 128, 189, 129, 242, 255, 227, 213, 255, 219, 128, 128, 128, 106, 126, 227, 252, 214, 209, 255, 255, 128, 128, 128,
            1, 98, 248, 255, 236, 226, 255, 255, 128, 128, 128, 181, 133, 238, 254, 221, 234, 255, 154, 128, 128, 128, 78, 134, 202, 247, 198, 180, 255, 219, 128, 128, 128,
            1, 185, 249, 255, 243, 255, 128, 128, 128, 128, 128, 184, 150, 247, 255, 236, 224, 128, 128, 128, 128, 128, 77, 110, 216, 255, 236, 230, 128, 128, 128, 128, 128,
            1, 101, 251, 255, 241, 255, 128, 128, 128, 128, 128, 170, 139, 241, 252, 236, 209, 255, 255, 128, 128, 128, 37, 116, 196, 243, 228, 255, 255, 255, 128, 128, 128,
            1, 204, 254, 255, 245, 255, 128, 128, 128, 128, 128, 207, 160, 250, 255, 238, 128, 128, 128, 128, 128, 128, 102, 103, 231, 255, 211, 171, 128, 128, 128, 128, 128,
            1, 152, 252, 255, 240, 255, 128, 128, 128, 128, 128, 177, 135, 243, 255, 234, 225, 128, 128, 128, 128, 128, 80, 129, 211, 255, 194, 224, 128, 128, 128, 128, 128,
            1, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128, 246, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128, 255, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
            198, 35, 237, 223, 193, 187, 162, 160, 145, 155, 62, 131, 45, 198, 221, 172, 176, 220, 157, 252, 221, 1, 68, 47, 146, 208, 149, 167, 221, 162, 255, 223, 128,
            1, 149, 241, 255, 221, 224, 255, 255, 128, 128, 128, 184, 141, 234, 253, 222, 220, 255, 199, 128, 128, 128, 81, 99, 181, 242, 176, 190, 249, 202, 255, 255, 128,
            1, 129, 232, 253, 214, 197, 242, 196, 255, 255, 128, 99, 121, 210, 250, 201, 198, 255, 202, 128, 128, 128, 23, 91, 163, 242, 170, 187, 247, 210, 255, 255, 128,
            1, 200, 246, 255, 234, 255, 128, 128, 128, 128, 128, 109, 178, 241, 255, 231, 245, 255, 255, 128, 128, 128, 44, 130, 201, 253, 205, 192, 255, 255, 128, 128, 128,
            1, 132, 239, 251, 219, 209, 255, 165, 128, 128, 128, 94, 136, 225, 251, 218, 190, 255, 255, 128, 128, 128, 22, 100, 174, 245, 186, 161, 255, 199, 128, 128, 128,
            1, 182, 249, 255, 232, 235, 128, 128, 128, 128, 128, 124, 143, 241, 255, 227, 234, 128, 128, 128, 128, 128, 35, 77, 181, 251, 193, 211, 255, 205, 128, 128, 128,
            1, 157, 247, 255, 236, 231, 255, 255, 128, 128, 128, 121, 141, 235, 255, 225, 227, 255, 255, 128, 128, 128, 45, 99, 188, 251, 195, 217, 255, 224, 128, 128, 128,
            1, 1, 251, 255, 213, 255, 128, 128, 128, 128, 128, 203, 1, 248, 255, 255, 128, 128, 128, 128, 128, 128, 137, 1, 177, 255, 224, 255, 128, 128, 128, 128, 128,
            253, 9, 248, 251, 207, 208, 255, 192, 128, 128, 128, 175, 13, 224, 243, 193, 185, 249, 198, 255, 255, 128, 73, 17, 171, 221, 161, 179, 236, 167, 255, 234, 128,
            1, 95, 247, 253, 212, 183, 255, 255, 128, 128, 128, 239, 90, 244, 250, 211, 209, 255, 255, 128, 128, 128, 155, 77, 195, 248, 188, 195, 255, 255, 128, 128, 128,
            1, 24, 239, 251, 218, 219, 255, 205, 128, 128, 128, 201, 51, 219, 255, 196, 186, 128, 128, 128, 128, 128, 69, 46, 190, 239, 201, 218, 255, 228, 128, 128, 128,
            1, 191, 251, 255, 255, 128, 128, 128, 128, 128, 128, 223, 165, 249, 255, 213, 255, 128, 128, 128, 128, 128, 141, 124, 248, 255, 255, 128, 128, 128, 128, 128, 128,
            1, 16, 248, 255, 255, 128, 128, 128, 128, 128, 128, 190, 36, 230, 255, 236, 255, 128, 128, 128, 128, 128, 149, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128,
            1, 226, 255, 128, 128, 128, 128, 128, 128, 128, 128, 247, 192, 255, 128, 128, 128, 128, 128, 128, 128, 128, 240, 128, 255, 128, 128, 128, 128, 128, 128, 128, 128,
            1, 134, 252, 255, 255, 128, 128, 128, 128, 128, 128, 213, 62, 250, 255, 255, 128, 128, 128, 128, 128, 128, 55, 93, 255, 128, 128, 128, 128, 128, 128, 128, 128,
            128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
            202, 24, 213, 235, 186, 191, 220, 160, 240, 175, 255, 126, 38, 182, 232, 169, 184, 228, 174, 255, 187, 128, 61, 46, 138, 219, 151, 178, 240, 170, 255, 216, 128,
            1, 112, 230, 250, 199, 191, 247, 159, 255, 255, 128, 166, 109, 228, 252, 211, 215, 255, 174, 128, 128, 128, 39, 77, 162, 232, 172, 180, 245, 178, 255, 255, 128,
            1, 52, 220, 246, 198, 199, 249, 220, 255, 255, 128, 124, 74, 191, 243, 183, 193, 250, 221, 255, 255, 128, 24, 71, 130, 219, 154, 170, 243, 182, 255, 255, 128,
            1, 182, 225, 249, 219, 240, 255, 224, 128, 128, 128, 149, 150, 226, 252, 216, 205, 255, 171, 128, 128, 128, 28, 108, 170, 242, 183, 194, 254, 223, 255, 255, 128,
            1, 81, 230, 252, 204, 203, 255, 192, 128, 128, 128, 123, 102, 209, 247, 188, 196, 255, 233, 128, 128, 128, 20, 95, 153, 243, 164, 173, 255, 203, 128, 128, 128,
            1, 222, 248, 255, 216, 213, 128, 128, 128, 128, 128, 168, 175, 246, 252, 235, 205, 255, 255, 128, 128, 128, 47, 116, 215, 255, 211, 212, 255, 255, 128, 128, 128,
            1, 121, 236, 253, 212, 214, 255, 255, 128, 128, 128, 141, 84, 213, 252, 201, 202, 255, 219, 128, 128, 128, 42, 80, 160, 240, 162, 185, 255, 205, 128, 128, 128,
            1, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128, 244, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128, 238, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128
        };

        // the probability that each default above is replaced in the frame header
        private static ReadOnlySpan<byte> CoeffsUpdateProba => new byte[]
        {
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            176, 246, 255, 255, 255, 255, 255, 255, 255, 255, 255, 223, 241, 252, 255, 255, 255, 255, 255, 255, 255, 255, 249, 253, 253, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 244, 252, 255, 255, 255, 255, 255, 255, 255, 255, 234, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 253, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 246, 254, 255, 255, 255, 255, 255, 255, 255, 255, 239, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 248, 254, 255, 255, 255, 255, 255, 255, 255, 255, 251, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 251, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 254, 253, 255, 254, 255, 255, 255, 255, 255, 255, 250, 255, 254, 255, 254, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            217, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 225, 252, 241, 253, 255, 255, 254, 255, 255, 255, 255, 234, 250, 241, 250, 253, 255, 253, 254, 255, 255, 255,
            255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 223, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 238, 253, 254, 254, 255, 255, 255, 255, 255, 255, 255,
            255, 248, 254, 255, 255, 255, 255, 255, 255, 255, 255, 249, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 253, 255, 255, 255, 255, 255, 255, 255, 255, 255, 247, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 252, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 253, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 254, 253, 255, 255, 255, 255, 255, 255, 255, 255, 250, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            186, 251, 250, 255, 255, 255, 255, 255, 255, 255, 255, 234, 251, 244, 254, 255, 255, 255, 255, 255, 255, 255, 251, 251, 243, 253, 254, 255, 254, 255, 255, 255, 255,
            255, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 236, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 251, 253, 253, 254, 254, 255, 255, 255, 255, 255, 255,
            255, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 254, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            248, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 250, 254, 252, 254, 255, 255, 255, 255, 255, 255, 255, 248, 254, 249, 253, 255, 255, 255, 255, 255, 255, 255,
            255, 253, 253, 255, 255, 255, 255, 255, 255, 255, 255, 246, 253, 253, 255, 255, 255, 255, 255, 255, 255, 255, 252, 254, 251, 254, 254, 255, 255, 255, 255, 255, 255,
            255, 254, 252, 255, 255, 255, 255, 255, 255, 255, 255, 248, 254, 253, 255, 255, 255, 255, 255, 255, 255, 255, 253, 255, 254, 254, 255, 255, 255, 255, 255, 255, 255,
            255, 251, 254, 255, 255, 255, 255, 255, 255, 255, 255, 245, 251, 254, 255, 255, 255, 255, 255, 255, 255, 255, 253, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 251, 253, 255, 255, 255, 255, 255, 255, 255, 255, 252, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 252, 255, 255, 255, 255, 255, 255, 255, 255, 255, 249, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 253, 255, 255, 255, 255, 255, 255, 255, 255, 250, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255
        };

        // 4x4 mode probabilities [above mode][left mode][9]
        private static ReadOnlySpan<byte> ModeProbas => new byte[]
        {
            231, 120, 48, 89, 115, 113, 120, 152, 112, 152, 179, 64, 126, 170, 118, 46, 70, 95, 175, 69, 143, 80, 85, 82, 72, 155, 103, 56, 58, 10, 171, 218, 189, 17, 13, 152, 114, 26, 17, 163, 44, 195, 21, 10, 173, 121, 24, 80, 195, 26, 62, 44, 64, 85, 144, 71, 10, 38, 171, 213, 144, 34, 26, 170, 46, 55, 19, 136, 160, 33, 206, 71, 63, 20, 8, 114, 114, 208, 12, 9, 226, 81, 40, 11, 96, 182, 84, 29, 16, 36,
            134, 183, 89, 137, 98, 101, 106, 165, 148, 72, 187, 100, 130, 157, 111, 32, 75, 80, 66, 102, 167, 99, 74, 62, 40, 234, 128, 41, 53, 9, 178, 241, 141, 26, 8, 107, 74, 43, 26, 146, 73, 166, 49, 23, 157, 65, 38, 105, 160, 51, 52, 31, 115, 128, 104, 79, 12, 27, 217, 255, 87, 17, 7, 87, 68, 71, 44, 114, 51, 15, 186, 23, 47, 41, 14, 110, 182, 183, 21, 17, 194, 66, 45, 25, 102, 197, 189, 23, 18, 22,
            88, 88, 147, 150, 42, 46, 45, 196, 205, 43, 97, 183, 117, 85, 38, 35, 179, 61, 39, 53, 200, 87, 26, 21, 43, 232, 171, 56, 34, 51, 104, 114, 102, 29, 93, 77, 39, 28, 85, 171, 58, 165, 90, 98, 64, 34, 22, 116, 206, 23, 34, 43, 166, 73, 107, 54, 32, 26, 51, 1, 81, 43, 31, 68, 25, 106, 22, 64, 171, 36, 225, 114, 34, 19, 21, 102, 132, 188, 16, 76, 124, 62, 18, 78, 95, 85, 57, 50, 48, 51,
            193, 101, 35, 159, 215, 111, 89, 46, 111, 60, 148, 31, 172, 219, 228, 21, 18, 111, 112, 113, 77, 85, 179, 255, 38, 120, 114, 40, 42, 1, 196, 245, 209, 10, 25, 109, 88, 43, 29, 140, 166, 213, 37, 43, 154, 61, 63, 30, 155, 67, 45, 68, 1, 209, 100, 80, 8, 43, 154, 1, 51, 26, 71, 142, 78, 78, 16, 255, 128, 34, 197, 171, 41, 40, 5, 102, 211, 183, 4, 1, 221, 51, 50, 17, 168, 209, 192, 23, 25, 82,
            138, 31, 36, 171, 27, 166, 38, 44, 229, 67, 87, 58, 169, 82, 115, 26, 59, 179, 63, 59, 90, 180, 59, 166, 93, 73, 154, 40, 40, 21, 116, 143, 209, 34, 39, 175, 47, 15, 16, 183, 34, 223, 49, 45, 183, 46, 17, 33, 183, 6, 98, 15, 32, 183, 57, 46, 22, 24, 128, 1, 54, 17, 37, 65, 32, 73, 115, 28, 128, 23, 128, 205, 40, 3, 9, 115, 51, 192, 18, 6, 223, 87, 37, 9, 115, 59, 77, 64, 21, 47,
            104, 55, 44, 218, 9, 54, 53, 130, 226, 64, 90, 70, 205, 40, 41, 23, 26, 57, 54, 57, 112, 184, 5, 41, 38, 166, 213, 30, 34, 26, 133, 152, 116, 10, 32, 134, 39, 19, 53, 221, 26, 114, 32, 73, 255, 31, 9, 65, 234, 2, 15, 1, 118, 73, 75, 32, 12, 51, 192, 255, 160, 43, 51, 88, 31, 35, 67, 102, 85, 55, 186, 85, 56, 21, 23, 111, 59, 205, 45, 37, 192, 55, 38, 70, 124, 73, 102, 1, 34, 98,
            125, 98, 42, 88, 104, 85, 117, 175, 82, 95, 84, 53, 89, 128, 100, 113, 101, 45, 75, 79, 123, 47, 51, 128, 81, 171, 1, 57, 17, 5, 71, 102, 57, 53, 41, 49, 38, 33, 13, 121, 57, 73, 26, 1, 85, 41, 10, 67, 138, 77, 110, 90, 47, 114, 115, 21, 2, 10, 102, 255, 166, 23, 6, 101, 29, 16, 10, 85, 128, 101, 196, 26, 57, 18, 10, 102, 102, 213, 34, 20, 43, 117, 20, 15, 36, 163, 128, 68, 1, 26,
            102, 61, 71, 37, 34, 53, 31, 243, 192, 69, 60, 71, 38, 73, 119, 28, 222, 37, 68, 45, 128, 34, 1, 47, 11, 245, 171, 62, 17, 19, 70, 146, 85, 55, 62, 70, 37, 43, 37, 154, 100, 163, 85, 160, 1, 63, 9, 92, 136, 28, 64, 32, 201, 85, 75, 15, 9, 9, 64, 255, 184, 119, 16, 86, 6, 28, 5, 64, 255, 25, 248, 1, 56, 8, 17, 132, 137, 255, 55, 116, 128, 58, 15, 20, 82, 135, 57, 26, 121, 40,
            164, 50, 31, 137, 154, 133, 25, 35, 218, 51, 103, 44, 131, 131, 123, 31, 6, 158, 86, 40, 64, 135, 148, 224, 45, 183, 128, 22, 26, 17, 131, 240, 154, 14, 1, 209, 45, 16, 21, 91, 64, 222, 7, 1, 197, 56, 21, 39, 155, 60, 138, 23, 102, 213, 83, 12, 13, 54, 192, 255, 68, 47, 28, 85, 26, 85, 85, 128, 128, 32, 146, 171, 18, 11, 7, 63, 144, 171, 4, 4, 246, 35, 27, 10, 146, 174, 171, 12, 26, 128,
            190, 80, 35, 99, 180, 80, 126, 54, 45, 85, 126, 47, 87, 176, 51, 41, 20, 32, 101, 75, 128, 139, 118, 146, 116, 128, 85, 56, 41, 15, 176, 236, 85, 37, 9, 62, 71, 30, 17, 119, 118, 255, 17, 18, 138, 101, 38, 60, 138, 55, 70, 43, 26, 142, 146, 36, 19, 30, 171, 255, 97, 27, 20, 138, 45, 61, 62, 219, 1, 81, 188, 64, 32, 41, 20, 117, 151, 142, 20, 21, 163, 112, 19, 12, 61, 195, 128, 48, 4, 24
        };

        // dequantisation factors of one segment
        private struct Quant
        {
            public int y1Dc, y1Ac, y2Dc, y2Ac, uvDc, uvAc;
        }

        // loop filter settings of one macroblock; limit 0 means unfiltered
        private struct FilterInfo
        {
            public byte limit, interior, hevThreshold;
            public bool inner;
        }

        // the frame header fields decoding needs after the header
        private struct Header
        {
            public bool updateMap, useSkip;
            public int skipProba, filterType, partitionMask;
            public byte segmentProba0, segmentProba1, segmentProba2;
        }

        // RFC 6386 boolean decoder over one partition of the chunk, in libwebp's form: range is kept minus one and the
        // value is refilled seven bytes at a time
        private struct BoolDecoder
        {
            public int pos, end, bits;
            public ulong value;
            public uint range;
            public bool eof;

            public void Init(ReadOnlySpan<byte> data, int start, int length)
            {
                pos = start;
                end = start + length;
                bits = -8;
                value = 0;
                range = 254;
                eof = false;
                Load(data);
            }

            private void Load(ReadOnlySpan<byte> data)
            {
                if (pos + 8 <= end)
                {
                    value = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(pos)) >> 8 | value << 56;
                    pos += 7;
                    bits += 56;
                }
                else if (pos < end)
                {
                    value = data[pos++] | value << 8;
                    bits += 8;
                }
                else if (!eof)
                {
                    value <<= 8;
                    bits += 8;
                    eof = true;
                }
                else bits = 0;
            }

            public int GetBit(ReadOnlySpan<byte> data, int prob)
            {
                uint r = range;
                if (bits < 0) Load(data);
                int p = bits;
                uint split = r * (uint)prob >> 8;
                int bit = 0;
                if ((uint)(value >> p) > split)
                {
                    r -= split;
                    value -= (ulong)(split + 1) << p;
                    bit = 1;
                }
                else r = split + 1;
                int shift = 7 ^ BitOperations.Log2(r);
                r <<= shift;
                bits -= shift;
                range = r - 1;
                return bit;
            }

            public int GetValue(ReadOnlySpan<byte> data, int count)
            {
                int v = 0;
                while (count-- > 0) v |= GetBit(data, 0x80) << count;
                return v;
            }

            public int GetSignedValue(ReadOnlySpan<byte> data, int count)
            {
                int v = GetValue(data, count);
                return GetBit(data, 0x80) == 1 ? -v : v;
            }
        }

        // Decodes a VP8 key frame of the given size into RGBA8 rows at the pitch, alpha opaque.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void Decode(ReadOnlySpan<byte> data, int width, int height, Span<byte> rgba, int pitch)
        {
            if (data.Length < 10) throw new ImageFormatException("WebP lossy frame header is truncated");
            uint tag = data[0] | (uint)data[1] << 8 | (uint)data[2] << 16;
            if ((tag & 1) != 0) throw new ImageFormatException("WebP lossy frame is not a key frame");
            if ((tag >> 1 & 7) > 3) throw new ImageFormatException($"WebP lossy profile {tag >> 1 & 7}");
            if ((tag >> 4 & 1) == 0) throw new ImageFormatException("WebP lossy frame is not displayable");
            if (data[3] != 0x9D || data[4] != 0x01 || data[5] != 0x2A) throw new ImageFormatException("WebP lossy start code is missing");
            int w = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6)) & 0x3FFF, h = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8)) & 0x3FFF;
            if (w != width || h != height) throw new ImageFormatException($"WebP lossy image is {w}x{h} inside a {width}x{height} frame");
            int part0 = (int)(tag >> 5);
            if (part0 > data.Length - 10) throw new ImageFormatException("WebP lossy first partition is longer than the chunk");

            int mbw = (width + 15) >> 4, mbh = (height + 15) >> 4;
            int ys = mbw * 16 + 5, cs = mbw * 8 + 1;
            byte[] probas = ArrayPool<byte>.Shared.Rent(ProbaCount);
            byte[] yPlane = ArrayPool<byte>.Shared.Rent(ys * (mbh * 16 + 1));
            byte[] uPlane = ArrayPool<byte>.Shared.Rent(cs * (mbh * 8 + 1));
            byte[] vPlane = ArrayPool<byte>.Shared.Rent(cs * (mbh * 8 + 1));
            byte[] intraTop = ArrayPool<byte>.Shared.Rent(mbw * 4);
            byte[] nzTop = ArrayPool<byte>.Shared.Rent(mbw * 9);
            short[] coeffs = ArrayPool<short>.Shared.Rent(384);
            FilterInfo[] filters = ArrayPool<FilterInfo>.Shared.Rent(mbw * mbh);
            try
            {
                Span<BoolDecoder> parts = stackalloc BoolDecoder[9];
                Span<Quant> quants = stackalloc Quant[4];
                Span<FilterInfo> strengths = stackalloc FilterInfo[8];
                ref BoolDecoder br = ref parts[8];
                br.Init(data, 10, part0);
                Header hdr = ReadHeader(data, ref br, parts, 10 + part0, quants, strengths, probas);

                // planes with a top border row of 127, a left border column of 129 and four spare columns on the right
                yPlane.AsSpan(0, ys).Fill(127);
                uPlane.AsSpan(0, cs).Fill(127);
                vPlane.AsSpan(0, cs).Fill(127);
                for (int y = 1; y <= mbh * 16; y++) yPlane[y * ys] = 129;
                for (int y = 1; y <= mbh * 8; y++)
                {
                    uPlane[y * cs] = 129;
                    vPlane[y * cs] = 129;
                }
                Array.Clear(intraTop, 0, mbw * 4);
                Array.Clear(nzTop, 0, mbw * 9);

                Span<byte> intraLeft = stackalloc byte[4];
                Span<byte> nzLeft = stackalloc byte[9];
                Span<byte> modes = stackalloc byte[16];
                Span<byte> codes = stackalloc byte[24];
                Span<short> dc = stackalloc short[16];
                for (int mby = 0; mby < mbh; mby++)
                {
                    ref BoolDecoder tokens = ref parts[mby & hdr.partitionMask];
                    intraLeft.Clear();
                    nzLeft.Clear();
                    for (int mbx = 0; mbx < mbw; mbx++)
                    {
                        // modes, from the first partition
                        int segment = 0;
                        if (hdr.updateMap)
                            segment = br.GetBit(data, hdr.segmentProba0) == 0 ? br.GetBit(data, hdr.segmentProba1) : br.GetBit(data, hdr.segmentProba2) + 2;
                        bool skip = hdr.useSkip && br.GetBit(data, hdr.skipProba) == 1;
                        bool i4x4 = br.GetBit(data, 145) == 0;
                        Span<byte> top = intraTop.AsSpan(mbx * 4, 4);
                        if (!i4x4)
                        {
                            int ymode = br.GetBit(data, 156) == 1 ? (br.GetBit(data, 128) == 1 ? TmPred : HePred) : (br.GetBit(data, 163) == 1 ? VePred : DcPred);
                            modes[0] = (byte)ymode;
                            top.Fill((byte)ymode);
                            intraLeft.Fill((byte)ymode);
                        }
                        else
                        {
                            for (int y = 0; y < 4; y++)
                            {
                                int ymode = intraLeft[y];
                                for (int x = 0; x < 4; x++)
                                {
                                    ReadOnlySpan<byte> prob = ModeProbas.Slice((top[x] * 10 + ymode) * 9, 9);
                                    int i = ModeTree[br.GetBit(data, prob[0])];
                                    while (i > 0) i = ModeTree[2 * i + br.GetBit(data, prob[i])];
                                    ymode = -i;
                                    top[x] = (byte)ymode;
                                }
                                top.CopyTo(modes.Slice(y * 4));
                                intraLeft[y] = (byte)ymode;
                            }
                        }
                        int uvMode = br.GetBit(data, 142) == 0 ? DcPred : br.GetBit(data, 114) == 0 ? VePred : br.GetBit(data, 183) == 1 ? TmPred : HePred;

                        // coefficients, from this row's token partition
                        Span<byte> nzAbove = nzTop.AsSpan(mbx * 9, 9);
                        bool nonZero = false;
                        if (!skip) nonZero = ReadResiduals(data, ref tokens, probas, quants[segment], i4x4, nzAbove, nzLeft, coeffs, codes, dc);
                        else
                        {
                            nzAbove.Slice(0, 8).Clear();
                            nzLeft.Slice(0, 8).Clear();
                            if (!i4x4) nzAbove[8] = nzLeft[8] = 0;
                            codes.Clear();
                        }
                        if (tokens.eof) throw new ImageFormatException("WebP lossy token partition ends early");
                        if (hdr.filterType > 0)
                        {
                            FilterInfo f = strengths[segment * 2 + (i4x4 ? 1 : 0)];
                            f.inner |= nonZero;
                            filters[mby * mbw + mbx] = f;
                        }

                        Reconstruct(yPlane, uPlane, vPlane, ys, cs, mbx, mby, mbw, i4x4, modes, uvMode, coeffs, codes);
                    }
                    if (br.eof) throw new ImageFormatException("WebP lossy first partition ends early");
                }

                if (hdr.filterType > 0) Filter(yPlane, uPlane, vPlane, ys, cs, mbw, mbh, hdr.filterType == 1, filters);
                Upsample(yPlane, uPlane, vPlane, ys, cs, width, height, rgba, pitch);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(probas);
                ArrayPool<byte>.Shared.Return(yPlane);
                ArrayPool<byte>.Shared.Return(uPlane);
                ArrayPool<byte>.Shared.Return(vPlane);
                ArrayPool<byte>.Shared.Return(intraTop);
                ArrayPool<byte>.Shared.Return(nzTop);
                ArrayPool<short>.Shared.Return(coeffs);
                ArrayPool<FilterInfo>.Shared.Return(filters);
            }
        }

        #region frame header
        // Segment, filter, partition, quantiser and probability headers, in stream order.
        private static Header ReadHeader(ReadOnlySpan<byte> data, ref BoolDecoder br, Span<BoolDecoder> parts, int partsAt, Span<Quant> quants,
            Span<FilterInfo> strengths, byte[] probas)
        {
            Header hdr = default;
            br.GetValue(data, 2);

            // segments
            bool useSegment = br.GetBit(data, 0x80) == 1, absoluteDelta = true;
            Span<int> segmentQ = stackalloc int[4];
            Span<int> segmentLevel = stackalloc int[4];
            hdr.segmentProba0 = hdr.segmentProba1 = hdr.segmentProba2 = 255;
            if (useSegment)
            {
                hdr.updateMap = br.GetBit(data, 0x80) == 1;
                if (br.GetBit(data, 0x80) == 1)
                {
                    absoluteDelta = br.GetBit(data, 0x80) == 1;
                    for (int s = 0; s < 4; s++) segmentQ[s] = br.GetBit(data, 0x80) == 1 ? br.GetSignedValue(data, 7) : 0;
                    for (int s = 0; s < 4; s++) segmentLevel[s] = br.GetBit(data, 0x80) == 1 ? br.GetSignedValue(data, 6) : 0;
                }
                if (hdr.updateMap)
                {
                    hdr.segmentProba0 = (byte)(br.GetBit(data, 0x80) == 1 ? br.GetValue(data, 8) : 255);
                    hdr.segmentProba1 = (byte)(br.GetBit(data, 0x80) == 1 ? br.GetValue(data, 8) : 255);
                    hdr.segmentProba2 = (byte)(br.GetBit(data, 0x80) == 1 ? br.GetValue(data, 8) : 255);
                }
            }
            if (br.eof) throw new ImageFormatException("WebP lossy segment header is truncated");

            // loop filter
            bool simple = br.GetBit(data, 0x80) == 1;
            int level = br.GetValue(data, 6), sharpness = br.GetValue(data, 3);
            int refDelta = 0, modeDelta = 0;
            bool useDelta = br.GetBit(data, 0x80) == 1;
            if (useDelta && br.GetBit(data, 0x80) == 1)
            {
                for (int i = 0; i < 4; i++)
                    if (br.GetBit(data, 0x80) == 1)
                    {
                        int v = br.GetSignedValue(data, 6);
                        if (i == 0) refDelta = v;
                    }
                for (int i = 0; i < 4; i++)
                    if (br.GetBit(data, 0x80) == 1)
                    {
                        int v = br.GetSignedValue(data, 6);
                        if (i == 0) modeDelta = v;
                    }
            }
            hdr.filterType = level == 0 ? 0 : simple ? 1 : 2;
            if (br.eof) throw new ImageFormatException("WebP lossy filter header is truncated");

            // token partitions
            int last = (1 << br.GetValue(data, 2)) - 1;
            hdr.partitionMask = last;
            int sizesAt = partsAt, at = partsAt + 3 * last, left = data.Length - at;
            if (left < 0) throw new ImageFormatException("WebP lossy partition sizes are truncated");
            for (int p = 0; p < last; p++)
            {
                int size = data[sizesAt + p * 3] | data[sizesAt + p * 3 + 1] << 8 | data[sizesAt + p * 3 + 2] << 16;
                if (size > left) size = left;
                parts[p].Init(data, at, size);
                at += size;
                left -= size;
            }
            if (left <= 0) throw new ImageFormatException("WebP lossy last token partition is empty");
            parts[last].Init(data, at, left);

            // quantisers
            int baseQ = br.GetValue(data, 7);
            int dqY1Dc = br.GetBit(data, 0x80) == 1 ? br.GetSignedValue(data, 4) : 0;
            int dqY2Dc = br.GetBit(data, 0x80) == 1 ? br.GetSignedValue(data, 4) : 0;
            int dqY2Ac = br.GetBit(data, 0x80) == 1 ? br.GetSignedValue(data, 4) : 0;
            int dqUvDc = br.GetBit(data, 0x80) == 1 ? br.GetSignedValue(data, 4) : 0;
            int dqUvAc = br.GetBit(data, 0x80) == 1 ? br.GetSignedValue(data, 4) : 0;
            for (int s = 0; s < 4; s++)
            {
                int q = useSegment ? segmentQ[s] + (absoluteDelta ? 0 : baseQ) : baseQ;
                ref Quant m = ref quants[s];
                m.y1Dc = DcTable[Clip(q + dqY1Dc, 127)];
                m.y1Ac = AcTable[Clip(q, 127)];
                m.y2Dc = DcTable[Clip(q + dqY2Dc, 127)] * 2;
                m.y2Ac = Math.Max(AcTable[Clip(q + dqY2Ac, 127)] * 101581 >> 16, 8);
                m.uvDc = DcTable[Clip(q + dqUvDc, 117)];
                m.uvAc = AcTable[Clip(q + dqUvAc, 127)];
            }

            // filter strength per segment and per 16x16 / 4x4 macroblock
            for (int s = 0; s < 4; s++)
            {
                int baseLevel = useSegment ? segmentLevel[s] + (absoluteDelta ? 0 : level) : level;
                for (int i4x4 = 0; i4x4 <= 1; i4x4++)
                {
                    int l = baseLevel;
                    if (useDelta)
                    {
                        l += refDelta;
                        if (i4x4 == 1) l += modeDelta;
                    }
                    l = Clip(l, 63);
                    FilterInfo f = default;
                    if (l > 0)
                    {
                        int interior = l;
                        if (sharpness > 0)
                        {
                            interior >>= sharpness > 4 ? 2 : 1;
                            if (interior > 9 - sharpness) interior = 9 - sharpness;
                        }
                        if (interior < 1) interior = 1;
                        f.interior = (byte)interior;
                        f.limit = (byte)(2 * l + interior);
                        f.hevThreshold = (byte)(l >= 40 ? 2 : l >= 15 ? 1 : 0);
                    }
                    f.inner = i4x4 == 1;
                    strengths[s * 2 + i4x4] = f;
                }
            }

            // coefficient probabilities and the skip flag
            br.GetBit(data, 0x80);
            ReadOnlySpan<byte> update = CoeffsUpdateProba, defaults = CoeffsProba0;
            for (int i = 0; i < ProbaCount; i++)
                probas[i] = (byte)(br.GetBit(data, update[i]) == 1 ? br.GetValue(data, 8) : defaults[i]);
            hdr.useSkip = br.GetBit(data, 0x80) == 1;
            if (hdr.useSkip) hdr.skipProba = br.GetValue(data, 8);
            return hdr;
        }

        private static int Clip(int v, int max) => v < 0 ? 0 : v > max ? max : v;
        #endregion

        #region coefficients
        // Reads one macroblock's coefficients and records per block whether it needs a full transform (2), the DC only
        // (1) or nothing (0); true when any block is non-zero.
        private static bool ReadResiduals(ReadOnlySpan<byte> data, ref BoolDecoder br, byte[] probas, in Quant q, bool i4x4, Span<byte> nzAbove,
            Span<byte> nzLeft, short[] coeffs, Span<byte> codes, Span<short> dc)
        {
            Array.Clear(coeffs, 0, 384);
            int first, type;
            if (!i4x4)
            {
                dc.Clear();
                int nz = ReadCoeffs(data, ref br, probas, TypeY2, nzAbove[8] + nzLeft[8], q.y2Dc, q.y2Ac, 0, dc);
                nzAbove[8] = nzLeft[8] = (byte)(nz > 0 ? 1 : 0);
                if (nz > 1) InverseWht(dc, coeffs);
                else
                {
                    short dc0 = (short)((dc[0] + 3) >> 3);
                    for (int i = 0; i < 256; i += 16) coeffs[i] = dc0;
                }
                first = 1;
                type = TypeI16Ac;
            }
            else
            {
                first = 0;
                type = TypeI4;
            }

            bool nonZero = false;
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    int n = y * 4 + x;
                    Span<short> block = coeffs.AsSpan(n * 16, 16);
                    int nz = ReadCoeffs(data, ref br, probas, type, nzLeft[y] + nzAbove[x], q.y1Dc, q.y1Ac, first, block);
                    nzLeft[y] = nzAbove[x] = (byte)(nz > first ? 1 : 0);
                    codes[n] = (byte)(nz > 1 ? 2 : block[0] != 0 ? 1 : 0);
                    nonZero |= codes[n] != 0;
                }
            for (int ch = 0; ch < 2; ch++)
                for (int y = 0; y < 2; y++)
                    for (int x = 0; x < 2; x++)
                    {
                        int n = 16 + ch * 4 + y * 2 + x;
                        Span<short> block = coeffs.AsSpan(n * 16, 16);
                        int nz = ReadCoeffs(data, ref br, probas, TypeChroma, nzLeft[4 + ch * 2 + y] + nzAbove[4 + ch * 2 + x], q.uvDc, q.uvAc, 0, block);
                        nzLeft[4 + ch * 2 + y] = nzAbove[4 + ch * 2 + x] = (byte)(nz > 0 ? 1 : 0);
                        codes[n] = (byte)(nz > 1 ? 2 : block[0] != 0 ? 1 : 0);
                        nonZero |= codes[n] != 0;
                    }
            return nonZero;
        }

        // Reads one block's tokens from position n into dequantised zigzag order; returns one past the last non-zero.
        private static int ReadCoeffs(ReadOnlySpan<byte> data, ref BoolDecoder br, byte[] probas, int type, int ctx, int dcq, int acq, int n, Span<short> output)
        {
            ReadOnlySpan<byte> bands = Bands, zigzag = Zigzag;
            int p = ((type * 8 + bands[n]) * 3 + ctx) * 11;
            for (; n < 16; n++)
            {
                if (br.GetBit(data, probas[p]) == 0) return n;
                while (br.GetBit(data, probas[p + 1]) == 0)
                {
                    if (++n == 16) return 16;
                    p = (type * 8 + bands[n]) * 33;
                }
                int next = (type * 8 + bands[n + 1]) * 33, v;
                if (br.GetBit(data, probas[p + 2]) == 0)
                {
                    v = 1;
                    p = next + 11;
                }
                else
                {
                    v = ReadLargeValue(data, ref br, probas, p);
                    p = next + 22;
                }
                if (br.GetBit(data, 0x80) == 1) v = -v;
                output[zigzag[n]] = (short)(v * (n > 0 ? acq : dcq));
            }
            return 16;
        }

        // A coefficient of 2 or more: the token tree past ONE, then the DCT_CAT extra bits.
        private static int ReadLargeValue(ReadOnlySpan<byte> data, ref BoolDecoder br, byte[] probas, int p)
        {
            if (br.GetBit(data, probas[p + 3]) == 0)
            {
                if (br.GetBit(data, probas[p + 4]) == 0) return 2;
                return 3 + br.GetBit(data, probas[p + 5]);
            }
            if (br.GetBit(data, probas[p + 6]) == 0)
            {
                if (br.GetBit(data, probas[p + 7]) == 0) return 5 + br.GetBit(data, 159);
                int v = 7 + 2 * br.GetBit(data, 165);
                return v + br.GetBit(data, 145);
            }
            int bit1 = br.GetBit(data, probas[p + 8]);
            int cat = 2 * bit1 + br.GetBit(data, probas[p + 9 + bit1]);
            ReadOnlySpan<byte> tab = CatProbas.Slice(CatStarts[cat], CatStarts[cat + 1] - CatStarts[cat]);
            int extra = 0;
            for (int i = 0; i < tab.Length; i++) extra += extra + br.GetBit(data, tab[i]);
            return extra + 3 + (8 << cat);
        }

        // Inverse Walsh-Hadamard of the Y2 block into the DC of each of the 16 luma blocks.
        private static void InverseWht(ReadOnlySpan<short> input, short[] coeffs)
        {
            Span<int> tmp = stackalloc int[16];
            for (int i = 0; i < 4; i++)
            {
                int a0 = input[i] + input[12 + i], a1 = input[4 + i] + input[8 + i];
                int a2 = input[4 + i] - input[8 + i], a3 = input[i] - input[12 + i];
                tmp[i] = a0 + a1;
                tmp[8 + i] = a0 - a1;
                tmp[4 + i] = a3 + a2;
                tmp[12 + i] = a3 - a2;
            }
            for (int i = 0; i < 4; i++)
            {
                int dc = tmp[i * 4] + 3;
                int a0 = dc + tmp[i * 4 + 3], a1 = tmp[i * 4 + 1] + tmp[i * 4 + 2];
                int a2 = tmp[i * 4 + 1] - tmp[i * 4 + 2], a3 = dc - tmp[i * 4 + 3];
                coeffs[i * 64] = (short)((a0 + a1) >> 3);
                coeffs[i * 64 + 16] = (short)((a3 + a2) >> 3);
                coeffs[i * 64 + 32] = (short)((a0 - a1) >> 3);
                coeffs[i * 64 + 48] = (short)((a3 - a2) >> 3);
            }
        }
        #endregion

        #region reconstruction
        // Predicts one macroblock into the planes and adds its residuals.
        private static void Reconstruct(byte[] yPlane, byte[] uPlane, byte[] vPlane, int ys, int cs, int mbx, int mby, int mbw, bool i4x4,
            ReadOnlySpan<byte> modes, int uvMode, short[] coeffs, ReadOnlySpan<byte> codes)
        {
            int yAt = (mby * 16 + 1) * ys + mbx * 16 + 1;
            if (i4x4)
            {
                // the top-right samples of the macroblock, repeated beside rows 3, 7 and 11 for the right column of blocks
                int topRight = yAt - ys + 16;
                if (mby > 0 && mbx == mbw - 1) yPlane.AsSpan(topRight, 4).Fill(yPlane[topRight - 1]);
                for (int r = 4; r < 16; r += 4) yPlane.AsSpan(topRight, 4).CopyTo(yPlane.AsSpan(topRight + r * ys));
                for (int n = 0; n < 16; n++)
                {
                    int at = yAt + (n >> 2) * 4 * ys + (n & 3) * 4;
                    Predict4(yPlane, at, ys, modes[n]);
                    AddResidual(coeffs.AsSpan(n * 16, 16), codes[n], yPlane, at, ys);
                }
            }
            else
            {
                Predict16(yPlane, yAt, ys, EdgeMode(mbx, mby, modes[0]), 16);
                for (int n = 0; n < 16; n++) AddResidual(coeffs.AsSpan(n * 16, 16), codes[n], yPlane, yAt + (n >> 2) * 4 * ys + (n & 3) * 4, ys);
            }

            int cAt = (mby * 8 + 1) * cs + mbx * 8 + 1;
            int chromaMode = EdgeMode(mbx, mby, uvMode);
            Predict16(uPlane, cAt, cs, chromaMode, 8);
            Predict16(vPlane, cAt, cs, chromaMode, 8);
            for (int n = 0; n < 4; n++)
            {
                int at = cAt + (n >> 1) * 4 * cs + (n & 1) * 4;
                AddResidual(coeffs.AsSpan((16 + n) * 16, 16), codes[16 + n], uPlane, at, cs);
                AddResidual(coeffs.AsSpan((20 + n) * 16, 16), codes[20 + n], vPlane, at, cs);
            }
        }

        // DC prediction without the samples the frame's top or left edge lacks.
        private static int EdgeMode(int mbx, int mby, int mode)
        {
            if (mode != DcPred) return mode;
            if (mbx == 0) return mby == 0 ? DcNoTopLeft : DcNoLeft;
            return mby == 0 ? DcNoTop : DcPred;
        }

        // 16x16 luma or 8x8 chroma prediction.
        private static void Predict16(byte[] plane, int at, int stride, int mode, int size)
        {
            int shift = size == 16 ? 4 : 3;
            switch (mode)
            {
                case VePred:
                    for (int y = 0; y < size; y++) plane.AsSpan(at - stride, size).CopyTo(plane.AsSpan(at + y * stride));
                    break;
                case HePred:
                    for (int y = 0; y < size; y++) plane.AsSpan(at + y * stride, size).Fill(plane[at + y * stride - 1]);
                    break;
                case TmPred:
                    TrueMotion(plane, at, stride, size);
                    break;
                default:
                    int sum = 0;
                    if (mode == DcPred || mode == DcNoLeft)
                        for (int i = 0; i < size; i++) sum += plane[at - stride + i];
                    if (mode == DcPred || mode == DcNoTop)
                        for (int i = 0; i < size; i++) sum += plane[at + i * stride - 1];
                    int value = mode == DcPred ? (sum + size) >> (shift + 1) : mode == DcNoTopLeft ? 0x80 : (sum + (size >> 1)) >> shift;
                    for (int y = 0; y < size; y++) plane.AsSpan(at + y * stride, size).Fill((byte)value);
                    break;
            }
        }

        private static void TrueMotion(byte[] plane, int at, int stride, int size)
        {
            int topLeft = plane[at - stride - 1];
            for (int y = 0; y < size; y++)
            {
                int row = at + y * stride, left = plane[row - 1] - topLeft;
                for (int x = 0; x < size; x++) plane[row + x] = Clip255(plane[at - stride + x] + left);
            }
        }

        // 4x4 luma prediction from the row above (with four top-right samples) and the column to the left.
        private static void Predict4(byte[] p, int at, int s, int mode)
        {
            int t = at - s;
            switch (mode)
            {
                case DcPred:
                {
                    int dc = 4;
                    for (int n = 0; n < 4; n++) dc += p[t + n] + p[at + n * s - 1];
                    for (int y = 0; y < 4; y++) p.AsSpan(at + y * s, 4).Fill((byte)(dc >> 3));
                    break;
                }
                case TmPred:
                    TrueMotion(p, at, s, 4);
                    break;
                case VePred:
                {
                    byte v0 = Avg3(p[t - 1], p[t], p[t + 1]), v1 = Avg3(p[t], p[t + 1], p[t + 2]);
                    byte v2 = Avg3(p[t + 1], p[t + 2], p[t + 3]), v3 = Avg3(p[t + 2], p[t + 3], p[t + 4]);
                    for (int y = 0; y < 4; y++)
                    {
                        int r = at + y * s;
                        p[r] = v0;
                        p[r + 1] = v1;
                        p[r + 2] = v2;
                        p[r + 3] = v3;
                    }
                    break;
                }
                case HePred:
                {
                    int a = p[t - 1], b = p[at - 1], c = p[at + s - 1], d = p[at + 2 * s - 1], e = p[at + 3 * s - 1];
                    p.AsSpan(at, 4).Fill(Avg3(a, b, c));
                    p.AsSpan(at + s, 4).Fill(Avg3(b, c, d));
                    p.AsSpan(at + 2 * s, 4).Fill(Avg3(c, d, e));
                    p.AsSpan(at + 3 * s, 4).Fill(Avg3(d, e, e));
                    break;
                }
                case RdPred:
                {
                    int i = p[at - 1], j = p[at + s - 1], k = p[at + 2 * s - 1], l = p[at + 3 * s - 1];
                    int x = p[t - 1], a = p[t], b = p[t + 1], c = p[t + 2], d = p[t + 3];
                    p[at + 3 * s] = Avg3(j, k, l);
                    p[at + 3 * s + 1] = p[at + 2 * s] = Avg3(i, j, k);
                    p[at + 3 * s + 2] = p[at + 2 * s + 1] = p[at + s] = Avg3(x, i, j);
                    p[at + 3 * s + 3] = p[at + 2 * s + 2] = p[at + s + 1] = p[at] = Avg3(a, x, i);
                    p[at + 2 * s + 3] = p[at + s + 2] = p[at + 1] = Avg3(b, a, x);
                    p[at + s + 3] = p[at + 2] = Avg3(c, b, a);
                    p[at + 3] = Avg3(d, c, b);
                    break;
                }
                case LdPred:
                {
                    int a = p[t], b = p[t + 1], c = p[t + 2], d = p[t + 3], e = p[t + 4], f = p[t + 5], g = p[t + 6], h = p[t + 7];
                    p[at] = Avg3(a, b, c);
                    p[at + 1] = p[at + s] = Avg3(b, c, d);
                    p[at + 2] = p[at + s + 1] = p[at + 2 * s] = Avg3(c, d, e);
                    p[at + 3] = p[at + s + 2] = p[at + 2 * s + 1] = p[at + 3 * s] = Avg3(d, e, f);
                    p[at + s + 3] = p[at + 2 * s + 2] = p[at + 3 * s + 1] = Avg3(e, f, g);
                    p[at + 2 * s + 3] = p[at + 3 * s + 2] = Avg3(f, g, h);
                    p[at + 3 * s + 3] = Avg3(g, h, h);
                    break;
                }
                case VrPred:
                {
                    int i = p[at - 1], j = p[at + s - 1], k = p[at + 2 * s - 1];
                    int x = p[t - 1], a = p[t], b = p[t + 1], c = p[t + 2], d = p[t + 3];
                    p[at] = p[at + 2 * s + 1] = Avg2(x, a);
                    p[at + 1] = p[at + 2 * s + 2] = Avg2(a, b);
                    p[at + 2] = p[at + 2 * s + 3] = Avg2(b, c);
                    p[at + 3] = Avg2(c, d);
                    p[at + 3 * s] = Avg3(k, j, i);
                    p[at + 2 * s] = Avg3(j, i, x);
                    p[at + s] = p[at + 3 * s + 1] = Avg3(i, x, a);
                    p[at + s + 1] = p[at + 3 * s + 2] = Avg3(x, a, b);
                    p[at + s + 2] = p[at + 3 * s + 3] = Avg3(a, b, c);
                    p[at + s + 3] = Avg3(b, c, d);
                    break;
                }
                case VlPred:
                {
                    int a = p[t], b = p[t + 1], c = p[t + 2], d = p[t + 3], e = p[t + 4], f = p[t + 5], g = p[t + 6], h = p[t + 7];
                    p[at] = Avg2(a, b);
                    p[at + 1] = p[at + 2 * s] = Avg2(b, c);
                    p[at + 2] = p[at + 2 * s + 1] = Avg2(c, d);
                    p[at + 3] = p[at + 2 * s + 2] = Avg2(d, e);
                    p[at + s] = Avg3(a, b, c);
                    p[at + s + 1] = p[at + 3 * s] = Avg3(b, c, d);
                    p[at + s + 2] = p[at + 3 * s + 1] = Avg3(c, d, e);
                    p[at + s + 3] = p[at + 3 * s + 2] = Avg3(d, e, f);
                    p[at + 2 * s + 3] = Avg3(e, f, g);
                    p[at + 3 * s + 3] = Avg3(f, g, h);
                    break;
                }
                case HdPred:
                {
                    int i = p[at - 1], j = p[at + s - 1], k = p[at + 2 * s - 1], l = p[at + 3 * s - 1];
                    int x = p[t - 1], a = p[t], b = p[t + 1], c = p[t + 2];
                    p[at] = p[at + s + 2] = Avg2(i, x);
                    p[at + s] = p[at + 2 * s + 2] = Avg2(j, i);
                    p[at + 2 * s] = p[at + 3 * s + 2] = Avg2(k, j);
                    p[at + 3 * s] = Avg2(l, k);
                    p[at + 3] = Avg3(a, b, c);
                    p[at + 2] = Avg3(x, a, b);
                    p[at + 1] = p[at + s + 3] = Avg3(i, x, a);
                    p[at + s + 1] = p[at + 2 * s + 3] = Avg3(j, i, x);
                    p[at + 2 * s + 1] = p[at + 3 * s + 3] = Avg3(k, j, i);
                    p[at + 3 * s + 1] = Avg3(l, k, j);
                    break;
                }
                default:
                {
                    int i = p[at - 1], j = p[at + s - 1], k = p[at + 2 * s - 1], l = p[at + 3 * s - 1];
                    p[at] = Avg2(i, j);
                    p[at + 2] = p[at + s] = Avg2(j, k);
                    p[at + s + 2] = p[at + 2 * s] = Avg2(k, l);
                    p[at + 1] = Avg3(i, j, k);
                    p[at + 3] = p[at + s + 1] = Avg3(j, k, l);
                    p[at + s + 3] = p[at + 2 * s + 1] = Avg3(k, l, l);
                    p[at + 2 * s + 3] = p[at + 2 * s + 2] = p[at + 3 * s] = p[at + 3 * s + 1] = p[at + 3 * s + 2] = p[at + 3 * s + 3] = (byte)l;
                    break;
                }
            }
        }

        private static byte Avg3(int a, int b, int c) => (byte)((a + 2 * b + c + 2) >> 2);

        private static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);

        private static byte Clip255(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        // Adds one block's inverse DCT to the prediction under it.
        private static void AddResidual(ReadOnlySpan<short> input, int code, byte[] plane, int dst, int stride)
        {
            if (code == 0) return;
            if (code == 1)
            {
                int dc = (input[0] + 4) >> 3;
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++) plane[dst + y * stride + x] = Clip255(plane[dst + y * stride + x] + dc);
                return;
            }
            Span<int> tmp = stackalloc int[16];
            for (int i = 0; i < 4; i++)
            {
                int a = input[i] + input[8 + i], b = input[i] - input[8 + i];
                int c = Mul2(input[4 + i]) - Mul1(input[12 + i]), d = Mul1(input[4 + i]) + Mul2(input[12 + i]);
                tmp[i * 4] = a + d;
                tmp[i * 4 + 1] = b + c;
                tmp[i * 4 + 2] = b - c;
                tmp[i * 4 + 3] = a - d;
            }
            for (int i = 0; i < 4; i++)
            {
                int dc = tmp[i] + 4;
                int a = dc + tmp[8 + i], b = dc - tmp[8 + i];
                int c = Mul2(tmp[4 + i]) - Mul1(tmp[12 + i]), d = Mul1(tmp[4 + i]) + Mul2(tmp[12 + i]);
                int row = dst + i * stride;
                plane[row] = Clip255(plane[row] + ((a + d) >> 3));
                plane[row + 1] = Clip255(plane[row + 1] + ((b + c) >> 3));
                plane[row + 2] = Clip255(plane[row + 2] + ((b - c) >> 3));
                plane[row + 3] = Clip255(plane[row + 3] + ((a - d) >> 3));
            }
        }

        private static int Mul1(int a) => ((a * 20091) >> 16) + a;

        private static int Mul2(int a) => (a * 35468) >> 16;
        #endregion

        #region loop filter
        // Filters every macroblock in raster order: left edge, inner vertical edges, top edge, inner horizontal edges.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Filter(byte[] yPlane, byte[] uPlane, byte[] vPlane, int ys, int cs, int mbw, int mbh, bool simple, FilterInfo[] filters)
        {
            for (int mby = 0; mby < mbh; mby++)
                for (int mbx = 0; mbx < mbw; mbx++)
                {
                    FilterInfo f = filters[mby * mbw + mbx];
                    int limit = f.limit;
                    if (limit == 0) continue;
                    int y = (mby * 16 + 1) * ys + mbx * 16 + 1;
                    if (simple)
                    {
                        if (mbx > 0) SimpleEdge(yPlane, y, 1, ys, limit + 4);
                        if (f.inner)
                            for (int k = 4; k < 16; k += 4) SimpleEdge(yPlane, y + k, 1, ys, limit);
                        if (mby > 0) SimpleEdge(yPlane, y, ys, 1, limit + 4);
                        if (f.inner)
                            for (int k = 4; k < 16; k += 4) SimpleEdge(yPlane, y + k * ys, ys, 1, limit);
                        continue;
                    }
                    int c = (mby * 8 + 1) * cs + mbx * 8 + 1, interior = f.interior, hev = f.hevThreshold;
                    if (mbx > 0)
                    {
                        MacroblockEdge(yPlane, y, 1, ys, 16, limit + 4, interior, hev);
                        MacroblockEdge(uPlane, c, 1, cs, 8, limit + 4, interior, hev);
                        MacroblockEdge(vPlane, c, 1, cs, 8, limit + 4, interior, hev);
                    }
                    if (f.inner)
                    {
                        for (int k = 4; k < 16; k += 4) InnerEdge(yPlane, y + k, 1, ys, 16, limit, interior, hev);
                        InnerEdge(uPlane, c + 4, 1, cs, 8, limit, interior, hev);
                        InnerEdge(vPlane, c + 4, 1, cs, 8, limit, interior, hev);
                    }
                    if (mby > 0)
                    {
                        MacroblockEdge(yPlane, y, ys, 1, 16, limit + 4, interior, hev);
                        MacroblockEdge(uPlane, c, cs, 1, 8, limit + 4, interior, hev);
                        MacroblockEdge(vPlane, c, cs, 1, 8, limit + 4, interior, hev);
                    }
                    if (f.inner)
                    {
                        for (int k = 4; k < 16; k += 4) InnerEdge(yPlane, y + k * ys, ys, 1, 16, limit, interior, hev);
                        InnerEdge(uPlane, c + 4 * cs, cs, 1, 8, limit, interior, hev);
                        InnerEdge(vPlane, c + 4 * cs, cs, 1, 8, limit, interior, hev);
                    }
                }
        }

        // The simple filter across 16 samples of one edge; step crosses the edge, along walks it.
        private static void SimpleEdge(byte[] b, int p, int step, int along, int threshold)
        {
            int t2 = 2 * threshold + 1;
            for (int i = 0; i < 16; i++, p += along)
                if (4 * Math.Abs(b[p - step] - b[p]) + Math.Abs(b[p - 2 * step] - b[p + step]) <= t2) Filter2(b, p, step);
        }

        // The normal filter on a macroblock edge: six taps where the edge is smooth, two where it has high variance.
        private static void MacroblockEdge(byte[] b, int p, int step, int along, int size, int threshold, int interior, int hev)
        {
            int t2 = 2 * threshold + 1;
            for (int i = 0; i < size; i++, p += along)
            {
                if (!NeedsFilter(b, p, step, t2, interior)) continue;
                if (HasHighVariance(b, p, step, hev)) Filter2(b, p, step);
                else Filter6(b, p, step);
            }
        }

        // The normal filter on an edge inside the macroblock: four taps, or two where it has high variance.
        private static void InnerEdge(byte[] b, int p, int step, int along, int size, int threshold, int interior, int hev)
        {
            int t2 = 2 * threshold + 1;
            for (int i = 0; i < size; i++, p += along)
            {
                if (!NeedsFilter(b, p, step, t2, interior)) continue;
                if (HasHighVariance(b, p, step, hev)) Filter2(b, p, step);
                else Filter4(b, p, step);
            }
        }

        private static bool NeedsFilter(byte[] b, int p, int step, int t2, int interior)
        {
            int p3 = b[p - 4 * step], p2 = b[p - 3 * step], p1 = b[p - 2 * step], p0 = b[p - step];
            int q0 = b[p], q1 = b[p + step], q2 = b[p + 2 * step], q3 = b[p + 3 * step];
            if (4 * Math.Abs(p0 - q0) + Math.Abs(p1 - q1) > t2) return false;
            return Math.Abs(p3 - p2) <= interior && Math.Abs(p2 - p1) <= interior && Math.Abs(p1 - p0) <= interior
                && Math.Abs(q3 - q2) <= interior && Math.Abs(q2 - q1) <= interior && Math.Abs(q1 - q0) <= interior;
        }

        private static bool HasHighVariance(byte[] b, int p, int step, int hev) =>
            Math.Abs(b[p - 2 * step] - b[p - step]) > hev || Math.Abs(b[p + step] - b[p]) > hev;

        private static void Filter2(byte[] b, int p, int step)
        {
            int p1 = b[p - 2 * step], p0 = b[p - step], q0 = b[p], q1 = b[p + step];
            int a = 3 * (q0 - p0) + Clamp(p1 - q1, -128, 127);
            int a1 = Clamp((a + 4) >> 3, -16, 15), a2 = Clamp((a + 3) >> 3, -16, 15);
            b[p - step] = Clip255(p0 + a2);
            b[p] = Clip255(q0 - a1);
        }

        private static void Filter4(byte[] b, int p, int step)
        {
            int p1 = b[p - 2 * step], p0 = b[p - step], q0 = b[p], q1 = b[p + step];
            int a = 3 * (q0 - p0);
            int a1 = Clamp((a + 4) >> 3, -16, 15), a2 = Clamp((a + 3) >> 3, -16, 15), a3 = (a1 + 1) >> 1;
            b[p - 2 * step] = Clip255(p1 + a3);
            b[p - step] = Clip255(p0 + a2);
            b[p] = Clip255(q0 - a1);
            b[p + step] = Clip255(q1 - a3);
        }

        private static void Filter6(byte[] b, int p, int step)
        {
            int p2 = b[p - 3 * step], p1 = b[p - 2 * step], p0 = b[p - step], q0 = b[p], q1 = b[p + step], q2 = b[p + 2 * step];
            int a = Clamp(3 * (q0 - p0) + Clamp(p1 - q1, -128, 127), -128, 127);
            int a1 = (27 * a + 63) >> 7, a2 = (18 * a + 63) >> 7, a3 = (9 * a + 63) >> 7;
            b[p - 3 * step] = Clip255(p2 + a3);
            b[p - 2 * step] = Clip255(p1 + a2);
            b[p - step] = Clip255(p0 + a1);
            b[p] = Clip255(q0 - a1);
            b[p + step] = Clip255(q1 - a2);
            b[p + 2 * step] = Clip255(q2 - a3);
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        #endregion

        #region upsampling
        // libwebp's fancy upsampler: each output row pairs its own chroma row with the nearest other one, weighted 3:1
        // and 9:3:3:1, into RGBA rows.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Upsample(byte[] yPlane, byte[] uPlane, byte[] vPlane, int ys, int cs, int width, int height, Span<byte> rgba, int pitch)
        {
            int y0 = ys + 1, c0 = cs + 1;
            UpsampleRows(yPlane, y0, -1, uPlane, vPlane, c0, c0, width, rgba, 0, -1);
            for (int y = 1; y + 1 < height; y += 2)
                UpsampleRows(yPlane, y0 + y * ys, y0 + (y + 1) * ys, uPlane, vPlane, c0 + ((y - 1) >> 1) * cs, c0 + ((y + 1) >> 1) * cs, width, rgba, y * pitch, (y + 1) * pitch);
            if ((height & 1) == 0)
            {
                int c = c0 + ((height - 1) >> 1) * cs;
                UpsampleRows(yPlane, y0 + (height - 1) * ys, -1, uPlane, vPlane, c, c, width, rgba, (height - 1) * pitch, -1);
            }
        }

        // One or two output rows between a top chroma row (near the top row) and a current one (near the bottom row);
        // u and v travel packed in the halves of one word.
        private static void UpsampleRows(byte[] yPlane, int topY, int bottomY, byte[] uPlane, byte[] vPlane, int topUv, int curUv, int len, Span<byte> rgba,
            int topDst, int bottomDst)
        {
            int lastPair = (len - 1) >> 1;
            uint tl = (uint)(uPlane[topUv] | vPlane[topUv] << 16), l = (uint)(uPlane[curUv] | vPlane[curUv] << 16);
            uint uv0 = (3 * tl + l + 0x00020002u) >> 2;
            ToRgba(yPlane[topY], uv0, rgba, topDst);
            if (bottomY >= 0)
            {
                uv0 = (3 * l + tl + 0x00020002u) >> 2;
                ToRgba(yPlane[bottomY], uv0, rgba, bottomDst);
            }
            for (int x = 1; x <= lastPair; x++)
            {
                uint t = (uint)(uPlane[topUv + x] | vPlane[topUv + x] << 16), uv = (uint)(uPlane[curUv + x] | vPlane[curUv + x] << 16);
                uint avg = tl + t + l + uv + 0x00080008u;
                uint diag12 = (avg + 2 * (t + l)) >> 3, diag03 = (avg + 2 * (tl + uv)) >> 3;
                ToRgba(yPlane[topY + 2 * x - 1], (diag12 + tl) >> 1, rgba, topDst + (2 * x - 1) * 4);
                ToRgba(yPlane[topY + 2 * x], (diag03 + t) >> 1, rgba, topDst + 2 * x * 4);
                if (bottomY >= 0)
                {
                    ToRgba(yPlane[bottomY + 2 * x - 1], (diag03 + l) >> 1, rgba, bottomDst + (2 * x - 1) * 4);
                    ToRgba(yPlane[bottomY + 2 * x], (diag12 + uv) >> 1, rgba, bottomDst + 2 * x * 4);
                }
                tl = t;
                l = uv;
            }
            if ((len & 1) == 0)
            {
                ToRgba(yPlane[topY + len - 1], (3 * tl + l + 0x00020002u) >> 2, rgba, topDst + (len - 1) * 4);
                if (bottomY >= 0) ToRgba(yPlane[bottomY + len - 1], (3 * l + tl + 0x00020002u) >> 2, rgba, bottomDst + (len - 1) * 4);
            }
        }

        // BT.601 limited-range YUV to RGB in libwebp's 14-bit fixed point.
        private static void ToRgba(int y, uint uv, Span<byte> rgba, int at)
        {
            int u = (int)(uv & 0xFF), v = (int)(uv >> 16);
            int luma = (y * 19077) >> 8;
            rgba[at] = Clip8(luma + ((v * 26149) >> 8) - 14234);
            rgba[at + 1] = Clip8(luma - ((u * 6419) >> 8) - ((v * 13320) >> 8) + 8708);
            rgba[at + 2] = Clip8(luma + ((u * 33050) >> 8) - 17685);
            rgba[at + 3] = 255;
        }

        private static byte Clip8(int v) => (byte)((v & ~16383) == 0 ? v >> 6 : v < 0 ? 0 : 255);
        #endregion
    }
}
