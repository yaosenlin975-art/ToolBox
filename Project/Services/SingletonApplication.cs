using System;
using System.Threading;
using System.Windows;

namespace ToolBox.Services;

public class SingletonApplication
{
    private static SingletonApplication instance;
    private Mutex mutex;
    private bool isNewInstance;

    public static SingletonApplication GetInstance(string appName, string[] args)
    {
        if (instance == null)
            instance = new SingletonApplication(appName);
        return instance;
    }

    private SingletonApplication(string appName)
    {
        mutex = new Mutex(true, appName, out isNewInstance);
    }

    public bool Register()
    {
        return isNewInstance;
    }

    public SingletonApplication AddSingletonFormListener(Window window)
    {
        return this;
    }

    public void NotifyNewInstance(string[] args)
    {
    }
}
