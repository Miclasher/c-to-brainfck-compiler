#include <stdio.h>

// Reads stdin until it ends (getchar returns 0 at EOF in most BF interpreters).
int main(void)
{
    int c = getchar();
    while (c != 0)
    {
        if (c >= 'a' && c <= 'z') c = c - 32;
        putchar(c);
        c = getchar();
    }
    return 0;
}
