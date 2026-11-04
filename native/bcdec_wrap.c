/* bcdec_wrap.c —— 把 bcdec.h 的 BC7 解码包成整图接口，输出 BGRA32。 */
#include <string.h>
#include <stdlib.h>
#define BCDEC_IMPLEMENTATION
#include "bcdec.h"

#ifdef _WIN32
#define API __declspec(dllexport)
#else
#define API
#endif

API int dsr_bc7_size(int w, int h)
{
    int bw = (w + 3) / 4, bh = (h + 3) / 4;
    return bw * bh * 16;
}

/* src: 压缩块数据；dst: w*h*4 的 BGRA 缓冲 */
API int dsr_bc7_decompress(const unsigned char* src, int srcLen,
                           unsigned char* dst, int w, int h)
{
    if (!src || !dst || w <= 0 || h <= 0) return -1;
    int bw = (w + 3) / 4, bh = (h + 3) / 4;
    if (srcLen < bw * bh * 16) return -2;

    unsigned char block[64];
    for (int by = 0; by < bh; ++by)
    {
        for (int bx = 0; bx < bw; ++bx)
        {
            const unsigned char* comp = src + (size_t)(by * bw + bx) * 16;
            memset(block, 0, sizeof(block));
            bcdec_bc7(comp, block, 16);   /* RGBA, 每行 16 字节 */

            for (int y = 0; y < 4; ++y)
            {
                int py = by * 4 + y;
                if (py >= h) continue;
                for (int x = 0; x < 4; ++x)
                {
                    int px = bx * 4 + x;
                    if (px >= w) continue;
                    const unsigned char* s = block + y * 16 + x * 4;
                    unsigned char* d = dst + ((size_t)py * w + px) * 4;
                    d[0] = s[2]; /* B */
                    d[1] = s[1]; /* G */
                    d[2] = s[0]; /* R */
                    d[3] = s[3]; /* A */
                }
            }
        }
    }
    return 0;
}
