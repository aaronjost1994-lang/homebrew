/* toolbox — depends on echoargs and exitcode (dependency-resolution demo). */
#include <stdio.h>

int main(void)
{
    printf("WinBrew demo toolbox 1.0.0\n");
    printf("Installed together with its dependencies:\n");
    printf("  echoargs  - shows how arguments arrive (try: echoargs \"a b\" c)\n");
    printf("  exitcode  - exits with the code you give (try: exitcode 7; echo $LASTEXITCODE)\n");
    return 0;
}
