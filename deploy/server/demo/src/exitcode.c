/* exitcode N — exits with N, to check that shims forward exit codes. */
#include <stdio.h>
#include <stdlib.h>

int main(int argc, char **argv)
{
    int code = argc > 1 ? atoi(argv[1]) : 0;
    printf("exiting with %d\n", code);
    return code;
}
