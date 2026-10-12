namespace ServiceUninstall
{
    public static class Program
    {
        private static int Main()
        {
            return ServiceHelper.Run(@"/u");
        }
    }
}
