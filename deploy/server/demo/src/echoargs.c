/* echoargs — prints each argument on its own line in [brackets], so you can
   see exactly how quoting survives the trip through a WinBrew shim. */
#include <stdio.h>
#include <windows.h>

int main(int argc, char **argv)
{
    printf("command line: %s\n", GetCommandLineA());
    printf("argc = %d\n", argc);
    for (int i = 0; i < argc; i++) {
        printf("argv[%d] = [%s]\n", i, argv[i]);
    }
    return 0;
}
