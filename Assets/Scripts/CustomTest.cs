using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using DetailComment;
using Unity.VisualScripting;
using UnityEngine;
using XLua;

[LuaCallCSharp]
public class CustomTest : MonoBehaviour
{
    private int testCount;

    public CustomTest(int _num) {
        testCount = _num;
    }

    public delegate void TestMyOwnDelegate(int a);
    void Start() {
        //TestTypeDetails();

        /*string a = "abc";
        Debug.LogFormat("index: {0}", a.IndexOf('['));*/

        //LoadLuaFile();

        TestMyOwnDelegate aDelegate = MethodOne;
        Debug.LogFormat("type: {0}", aDelegate.Method.GetType());
    }

    public void MethodOne(int num) {
        num = 100;
    }

    public static void TestStaticMethod(int num) {
        num = 200;
    }

    //加载Lua文件的方式
    public void LoadLuaFile() {
        /* Resources.Load无法加载“xxx.lua”文件
        string filename = "a";
        var textAsset =  Resources.Load(filename) as TextAsset;
        Debug.LogFormat("content: {0}", textAsset.text);*/

        var path = Application.dataPath + "/Resources/a.lua";
        var content = File.ReadAllText(path);
        Debug.LogFormat("content: {0}", content);
    }

    //测试Type中的诸多特性
    void TestTypeDetails() {
        Src01 temp = new Src01();
        Debug.LogFormat("temp type: {0}, namespace: {1}", temp.GetType(), temp.GetType().Namespace);

        Src01.A temp02 = new Src01.A();
        Debug.LogFormat("temp02 type: {0}, namespace: {1}", temp02.GetType(), temp02.GetType().Namespace);
        if (temp02.GetType().IsNested) {
            Debug.LogFormat("temp02 is a nested type.");
        }

        var tempType = temp.GetType();
        Debug.LogFormat("循环type: {0}", tempType.GetType());

        int a = 100;
        Debug.LogFormat("第一层type: {0}", a.GetType());
        var aType = a.GetType();
        Debug.LogFormat("第二层type: {0}", aType.GetType());
        Debug.LogFormat("是否是valueType: {0}, {1}", a.GetType().IsValueType, a.GetType().GetType().IsValueType);
    }
}

#region 测试type中的诸多特性
namespace DetailComment
{
    public class Src01
    {
        public class A
        {


        }
    }
}
#endregion