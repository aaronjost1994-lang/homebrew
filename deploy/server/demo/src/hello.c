/* hello — the WinBrew demo package. HELLO_VERSION is set at compile time. */
#include <stdio.h>

int main(int argc, char **argv)
{
    printf("Hello from the WinBrew demo repository! (hello %s)\n", HELLO_VERSION);
    if (argc > 1) {
        printf("You said:");
        for (int i = 1; i < argc; i++) {
            printf(" %s", argv[i]);
        }
        printf("\n");
    }
    return 0;
}
