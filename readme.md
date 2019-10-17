# 一种Shader变体收集和打包编译优化的思路

## 介绍

### 什么是变体

引用Unity官方文档的解释: [ShaderVariant](https://docs.unity3d.com/ScriptReference/ShaderVariantCollection.ShaderVariant.html)
> In Unity, many shaders internally have multiple "variants", to account for different light modes, lightmaps, shadows and so on. These variants are indentified by a shader pass type, and a set of shader keywords.

Unity的shader资源不仅含有GPU上执行的着色器代码，还包含了渲染状态，属性的定义，以及对应不同渲染管线不同渲染阶段用于的着色器代码，每一小段代码也可能会有不同的编译参数，以对应不同的渲染功能

shader片段中，最显著的特征就是拥有这些预编译开关如：

```
#pragma multi_compile_fwdbase // unity 内置前向管线编译设置集合，控制光照，阴影等很多相关功能
#pragma shader_feature _USE_FEATURE_A // 自定义功能开关
#pragma multi_compile _USE_FUNCTION_A _USE_FUNCTION_B // 自定义多编译选项
```

有了这些编译开关标记，我们就可以只写很少的shader代码，从而依附在这份骨架代码上，来实现含有细微差异功能的变种shader代码。当然功能越多，这些变体数量也成指数级增长，如何控制这些变体可能产生的数量，也需要较为丰富的经验和技巧。

### 为什么要收集变体

游戏初始化的时候一般需要提前把渲染要使用的Shader全部都加载进来，以降低游戏运行时及时加载和编译带来的卡顿，这时候我们可以调用Shader.WarmupAllShaders来把当前已经加载到内存的shader全部编译一次，包含所有的变体。

随着项目渲染效果的丰富，shader变体变得越来越多，粗暴的调用全加载接口，会导致游戏的启动时间变得更长，影响游戏体验。

后来Unity进入了变体集合[ShaderVariantCollection](https://docs.unity3d.com/ScriptReference/ShaderVariantCollection.html)来取代上面的粗暴全加载接口，达到按需加载，提高加载速度

官方解释中最为关键的内容如下：

> This is used for shader preloading ("warmup"), so that a game can make sure "actually required" shader variants are loaded at startup (or level load time), to avoid shader compilation related hiccups later on in the game.

也就是说变体记录的是游戏实际上使用到的变体集合，那么对其进行按需加载能够大大提高游戏加载速度

### 其他一些理解

从官方文档上我们得知，变体集合是用于预加载Shader，但是并没有提及打包发布过程中的编译，以及如何筛选实际使用的编译进入AssetBundle。

在实际发布的游戏包里的Shader资源，如果缺失了需要的某个变体，那么就如同Shader丢失一样，渲染的对象将一片紫色

那么为什么会出现Shader变体丢失的情况呢？通常都由与AssetBundle的分包打包导致。unity内部搜集实际使用的变体是通过扫描使用shader的材质，以及场景渲染器上的光照参数来综合获取。为了实现AssetBundle的更新，我们通常会把Shader作为单独的资源放在一个独立的AssetBundle中，而其他引用这些Shader的材质和场景，将作为AssetBundle的依赖加载。shader一旦脱离了了使用他们的载体，unity在打包的时候无法全盘考虑那些变体需要实际发布，从而随机性的出现恼人的变体丢失现象。

网上的一些解决变体丢失办法：
1. 把Shader和使用他们的材质一同打到一个AssetBundle
2. 在Editor通跑一遍整个项目场景，Unity会把搜集到的所有shader以及变体记录下来，然后把这个信息保存成变体集合，把它和shader一并打包

Unity在Project Settings面板最下面隐藏了这个最为重要的功能：

![](./images/20191017153331.png)


上面提到的方法从多数情况下都能正常工作，但是：
- 方法一中，光从材质上不能获得完整的变体使用记录，还和实际渲染器和所在场景的全局光照有关系
- 方法二中，人为搜集始终有漏网之鱼，而且unity保存出来的shader变体全在一起，如果不经过拆分，打包分包策略会有些许影响

### 我的解决办法

**项目中的用于渲染的资源一般来说只有三大类**
1. 场景
2. 动态加载的模型，角色，特效等
3. UI & UIEffect

其中，一般UI都是直接使用UGUI内置的着色器，变体都是通过multi_compile提供，这种编译开关可以保证无论此开关在材质中是否使用，变体都会得到编译并且经入发布真机包。

故最终我们只需要考虑Shader的两种使用情形：场景中动态加载和场景静态资源。

**实现一个自动shader变体收集器，步骤如下：**

1. 把当前需要打包的资源路径搜集起来（按照工程发布设置，如多语言，渠道）
2. 通过依赖关系，把动态加载的prefab这类资源依赖的材质路径搜集起来
3. 打开一个新的空场景，创建一个游戏场景中的动态光源环境，如实时平行光
4. 反射调用ShaderUtil.ClearCurrentShaderVariantCollection清空当前项目搜集到的变体，我们需要重新搜集一次
5. 场景中创建一个用于渲染的相机
6. 在场景中创建一堆sphere几何体，并排列整齐，然后把渲染相机对齐它们，并保证他们都可以看见
7. 分批次把这些材质资源赋予这些sphere几何体，渲染一帧
8. 渲染完毕之后，依次打开场景，并且设置好一个全景相机视角并渲染
9. 这样基本上项目上的shader变体已经搜集完毕，反射调用ShaderUtil.SaveCurrentShaderVariantCollection保存到一个整体变体集合资源中去
10. 自动搜集工具任务完成

**有了这个变体集合，就OK了吗？**

不，任务只完成了一半。有些自定义的shader，尤其是那些**只**通过UsePass被引用到的Shader，并没有出现在任何材质资源上，故不能被Unity搜集到它们的变体。

举个例子：

有三个Shader: 
- ABC.shader
- InternalA.shader
- InternalB.shader

ABC使用了InternalA, InternalB的内部pass, ABC并没有实际的代码片段。这种情况下，Unity搜集到的变体集合，是属于ABC的，而没有分别区分InternalA， InternalB，如果你直接拿这份Unity的导出结果，很有可能导致变体丢失。

我们需要把Unity导出的变体集合挨个拆分成零散资源，一来可以创建那些被关联进来的shader变体集合，二来也可以方便打包粒度拆分。

**继续**

在继续之前，先做一些准备工作：

1. 通过反射ShaderUtil.OpenShaderCombinations( shader, usedBySceneOnly = true )可以打开一个unity生成的Library/ParsedCombinations-xxx.shader文件，通过文本解析，可以得到所有有效的builtin, shader_features, multi_compiles这三大类keyword，以及代码snippets标记
2. 通过反射方法读取ShaderVariantCollection中每一组shader的变体集
3. 做好一些缓存工作，便于后期重复获取这些信息



为了让下面的逻辑表述更为清晰，用伪代码表示：
```
// 开始拆分总集，并为所有的shder创建独立变体集
ShaderVariantCollection unityVAC; // unity导出的总集

foreach ( curSVC in unityVAC ) {

    // 集合中当前一个子集shader
    var cur_shader = curSVC.shader;
    // 当前shader的所有变体
    var cur_shaderVariants = curSVC.variants;
    
    // 为当前创建新的独立变体集
    var va = new ShaderVariantCollection();
    
    // 尝试把这些变体拷贝到新的变体集中去
    foreach ( cur_v in cur_shaderVariants ) {
        try {
            var realSV = new ShaderVariantCollection.ShaderVariant( cur_v.shader, cur_v.passType, cur_v.keywords );
            va.Add( realSV );
        } catch ( ... ) {
            // 说明此变体不属于当前创建的shader的指定pass类型
            // 走到这里，一般都因unity搜集的变体属于依赖项
        }
    }
    Save( va );
    
    // 获取依赖，通过UsePass, Fallback进来的
    // 依次为子shader创建或更新变体集
    var child_shaders = GetDependencies( GetAssetPath( cur_shader ) );
    foreach ( child_shader in child_shaders ) {
        // 被依赖shader可以被多个不同shader多次依赖，这里要注意缓存
        var child_va = TryGet_New_ShaderVariantCollection( child_shader );
        // 把变体依次传入测试是否同时属于依赖child_shader
        foreach ( cur_v in cur_shaderVariants ) {
        
            var _keywords = copy( cur_v.keywords );
            
            // 此变体中可能含有不属于child_shader的关键字
            // 通过之前提供的解析ParsedCombinations文件，排除它们
            RemoveInvalidKeyword( _keywords, child_shader );
            
            try {
                var realSV = new ShaderVariantCollection.ShaderVariant( child_shader, cur_v.passType, _keywords );
                // 注意去重
                if ( !child_va.Contains( realSV ) ) {
                    child_va.Add( realSV );
                }
            } catch ( ... ) {
                // ...
            }
        }
    }
    
    // 保存所有的被依赖进来的shader的变体集合
    // ...
    
    // 这里有一些问题:
    // 1. 由于变体的从属pass不能获取完整，
    //（既没有passName，也没有passIndex）
    // 所以不能精准地为依赖项创建变体，所以上面代码中只要变体合法，就当他使用了
    // 2. 一个shader既可以直接被材质使用，被Unity搜集到变体，
    // 也可能被其他Shader引用，那么被引用部分产生的变体能否被unity搜集到，
    // 还需要进一步测试验证
}

```

这样经过上面一番折腾，希望能创建最为完整的变体使用记录，开始进行下一阶段折腾

**编译时间优化**

我发现有了变体集之后，打包shader时，依然对shader进行了长时间的编译，就算加上了multi_compile数量的预估，也超过变体集中生命数量太多。由此我从官方文档对变体集合的解释文字上推断，变体集它只能用于预加载，和指定shader变体的使用子集，至于编译，那是另外一个资源处理阶段，需要我们自行过滤排除。

unity2018.2引入了一个可编程的shader变体移除管道：IPreprocessShaders.OnProcessShader，有了这个接口，我们就可以在Unity编译Shader的时候收到回调通知，我们可以实现自己的Shader变体删除逻辑，进一步减少编译时间。

项目里面可以通过实现多个IPreprocessShaders接口对象，Unity在编译shader时，会自行创建这些处理器实例，并执行其中的回调接口，我们需要在这些回调中，对传入的参数进行排除

一个例子：
```
/// 一个简单排除Unity内建变体编译的处理器
class BuiltinShaderPreprocessor : IPreprocessShaders {
    static ShaderKeyword[] s_uselessKeywords;
    public int callbackOrder {
        get { return 0; } // 可以指定多个处理器之间回调的顺序
    }
    static BuiltinShaderPreprocessor() {
        s_uselessKeywords = new ShaderKeyword[] {
            new ShaderKeyword( "DIRLIGHTMAP_COMBINED" ),
            new ShaderKeyword( "LIGHTMAP_SHADOW_MIXING" ),
            new ShaderKeyword( "SHADOWS_SCREEN" ),
        };
    }
    public void OnProcessShader( Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data ) {
        for ( int i = data.Count - 1; i >= 0; --i ) {
            for ( int j = 0; j < s_uselessKeywords.Length; ++j ) {
                if ( data[ i ].shaderKeywordSet.IsEnabled( s_uselessKeywords[ j ] ) ) {
                    data.RemoveAt( i );
                    break;
                }
            }
        }
    }
}
```

我们需要创建更为细致和精准的编译排除逻辑（代码片段不完整）
```
class ShaderPreprocessor : IPreprocessShaders {
    public void OnProcessShader( Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data ) {
        // 跳过处理系统shader，不处理
        // return;
        
        // 读取对应shader的变体集:
        // 上一步我们为每一个使用到的shader都创建了独立的编译集合
        // 获取指定shader的编译信息
        
        var comb = ShaderUtils.ParseShaderCombinations( shader, true );
        // 跳过一些完全Use其他shader，自己不含有代码的shader， 不处理
        // return;
        
        // 反向遍历，利于删除操作
        for ( int i = data.Count - 1; i >= 0; --i ) {
            // 当前编译单元中的变体关键字列表
            var _keywords = data[ i ].shaderKeywordSet.GetShaderKeywords();
            // 只剔除有关键字的情形，减少代码复杂度
            // 实际上，无关键字的变体也可能被丢弃不用，简单舍弃这次剔除操作并不会增加太多编译负担
            if ( _keywords.Length > 0 ) {
                var keywordList = new HashSet<String>();
                for ( int j = 0; j < _keywords.Length; ++j ) {
                    var name = _keywords[ j ].GetKeywordName();
                    fullKeywords.Add( name );
                    if ( snippetCombinations.multi_compiles != null ) {
                        if ( Array.IndexOf( snippetCombinations.multi_compiles, name ) < 0 ) {
                            // 排除multi_compiles编译宏，这些是必须使用的，不能剔除
                            // 这里只添加不含multi_compile关键字
                            keywordList.Add( name );
                        }
                    }
                }
                if ( keywordList.Count > 0 ) {
                    // 说明这个变体的关键字时可以被剔除编译的
                    // 进一步判定：
                    // 由这一关键字序列构成的变体，是否在我们提前存储的变体集资源中出现
                    
                    // 在遍历判定已经使用的变体集的时候，注意要把含有multi_compile项
                    // 的关键字去掉，在无序对比，如果能完全匹配，则说明当前次编译的
                    // shader变体可能会使用，否则就剔除
                    // ...
                    
                    var matched = false;
                    // 遍历所有从项目中搜集到的变体
                    for ( int n = 0; n < rawVariants.Count; ++n ) {
                        var variant = rawVariants[ n ];
                        var matchCount = -1;
                        var mismatchCount = 0;
                        var skipCount = 0;
                        if ( variant.shader == shader && variant.passType == snippet.passType ) {
                            matchCount = 0;
                            
                            // 需要说明一下：
                            // 查找匹配的变体时，需要排除multi_compiles关键字
                            // snippetCombinations数据从手工解析ParsedCombinations-XXX.shader而来
                            // 如果直接调用ShaderUtil.GetShaderVariantEntries，可能会因为全变体数量过大而内存爆掉 
                            
                            for ( var m = 0; m < variant.keywords.Length; ++m ) {
                                var keyword = variant.keywords[ m ];
                                if ( Array.IndexOf( snippetCombinations.multi_compiles, keyword ) < 0 ) {
                                    if ( keywordList.Contains( keyword ) ) {
                                        ++matchCount;
                                    } else {
                                        ++mismatchCount;
                                        break;
                                    }
                                } else {
                                    ++skipCount;
                                }
                            }
                        }
                        if ( matchCount >= 0 && mismatchCount == 0 && matchCount + skipCount == keywordList.Count ) {
                            matched = true;
                            break;
                        }
                    }
                    if ( !matched ) {
                        data.RemoveAt( i );
                    }
                }
            }
        }
    }
}
```

**结束**

经过上面一系列的操作，shader变体收集流程和编译时间都得到的优化。但在实现整个了流程的过程中，使用了不少unity并不常用的编辑器API，由于部分过程获取的信息不完整，导致最终的结果肯定还有一些难以察觉的错误，该方法也需要进一步研究和改进。

