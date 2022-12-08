using System.Collections;
using System.Collections.Generic;
using DetailComment;
using UnityEngine;

public class CustomTest : MonoBehaviour
{
    void Start() {
        //TestTypeDetails();

        int a = 100, b = 100;
        Debug.LogFormat("a, b比较结果：{0}", ReferenceEquals(a, b));
        string c = "hello", d = "hello";
        Debug.LogFormat("c, d比较结果：{0}", ReferenceEquals(c, d));
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