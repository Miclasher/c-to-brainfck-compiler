#include <stdio.h>

int is_prime(int n)
{
    int d = 2, ok = 1;
    if (n < 2) ok = 0;
    while (d * d <= n && ok)
    {
        if (n % d == 0) ok = 0;
        d++;
    }
    return ok;
}

int main(void)
{
    int n;
    for (n = 1; n < 60; n++)
    {
        if (is_prime(n)) printf("%d ", n);
    }
    putchar('\n');
    return 0;
}
