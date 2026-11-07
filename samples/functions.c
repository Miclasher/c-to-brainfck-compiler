#include <stdio.h>

int g = 5; // globals live in fixed tape cells

int square(int x) { return x * x; }
int add(int a, int b) { return a + b; }

int fact(int n)
{
    int r = 1;
    while (n > 1)
    {
        r *= n;
        n--;
    }
    return r;
}

int main(void)
{
    printf("%d %d %d\n", square(7), add(square(2), add(3, 4)), g);
    printf("5! = %d\n", fact(5));
    int arr[5] = {1, 2, 3};
    arr[3] = arr[0] + arr[1] + arr[2];
    printf("%d %d\n", arr[3], arr[4]);
    int x = 10;
    printf("%d\n", x > 5 ? 111 : 222);
    return 0;
}
