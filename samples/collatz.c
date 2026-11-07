#include <stdio.h>

int main(void)
{
    int n = 7, steps = 0;
    while (n != 1)
    {
        if (n % 2 == 0) n = n / 2;
        else n = 3 * n + 1; // 7 peaks at 52
        steps++;
        if (steps > 100) break;
    }
    printf("%d\n", steps);
    return 0;
}
