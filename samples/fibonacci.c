#include <stdio.h>

// Cells are 8-bit, so values wrap at 256: the first 13 numbers fit.
int main(void)
{
    int a = 0, b = 1, i;
    for (i = 0; i < 13; i++)
    {
        printf("%d ", a);
        int t = a + b;
        a = b;
        b = t;
    }
    putchar('\n');
    return 0;
}
