using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine.Rendering;

using Debug = UnityEngine.Debug;

namespace ShaderVariantCollector {

    public class CustomShaderPreprocessor : IPreprocessShaders {

        public delegate void ProcessShaderCallback( Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data );

        public static ProcessShaderCallback OnProcessShaderCallback = null;

        public int callbackOrder {
            get { return -1; }
        }

        public void OnProcessShader( Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data ) {
            if ( OnProcessShaderCallback != null ) {
                try {
                    OnProcessShaderCallback( shader, snippet, data );
                } catch ( Exception e ) {
                    Debug.LogException( e );
                }
            }
        }
    }

    class BuiltinShaderPreprocessor : IPreprocessShaders {
        static ShaderKeyword[] s_uselessKeywords;
        static List<ShaderCompilerData> s_cache = new List<ShaderCompilerData>();
        public int callbackOrder {
            get { return 0; }
        }
        static BuiltinShaderPreprocessor() {
            s_uselessKeywords = new ShaderKeyword[] {
                new ShaderKeyword( "SHADOWS_CUBE" ),
            };
        }
        public void OnProcessShader( Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data ) {
            if ( snippet.passType == PassType.ForwardAdd ||
                snippet.passType == PassType.LightPrePassBase ||
                snippet.passType == PassType.LightPrePassFinal ||
                snippet.passType == PassType.Deferred ||
                snippet.passType == PassType.ScriptableRenderPipeline ||
                snippet.passType == PassType.ScriptableRenderPipelineDefaultUnlit ||
                snippet.passType == PassType.Meta ||
                snippet.passType == PassType.MotionVectors ) {
                data.Clear();
                return;
            }
            s_cache.Clear();
            s_cache.AddRange( data );
            data.Clear();
            for ( int i = 0; i < s_cache.Count; ++i ) {
                var d = s_cache[ i ];
                for ( int j = 0; j < s_uselessKeywords.Length; ++j ) {
                    if ( d.shaderKeywordSet.IsEnabled( s_uselessKeywords[ j ] ) ) {
                        continue;
                    }
                }
                data.Add( d );
            }
        }
    }

    public class ShaderPreprocessor : IPreprocessShaders {

        public class ShaderVariantInfo {
            public ShaderVariantCollection variantCollection;
            public ShaderVariantCollectionHelper.RawData rawVariantCollection;
            public ShaderParsedCombinations combinations;
            public HashSet<String> shader_features;
            public HashSet<String> multi_compiles;
            public HashSet<String> builtins;
            public void MergeAllShaderKeywords() {
                if ( combinations.snippets != null ) {
                    var count = combinations.snippets.Count;
                    for ( int i = 0; i < count; ++i ) {
                        var snippet = combinations.snippets[ i ];
                        if ( snippet.builtins != null ) {
                            foreach ( var k in snippet.builtins ) {
                                builtins = builtins ?? new HashSet<String>();
                                builtins.Add( k );
                            }
                        }
                        if ( snippet.multi_compiles != null ) {
                            foreach ( var k in snippet.multi_compiles ) {
                                multi_compiles = multi_compiles ?? new HashSet<String>();
                                multi_compiles.Add( k );
                            }
                        }
                        if ( snippet.shader_features != null ) {
                            foreach ( var k in snippet.shader_features ) {
                                shader_features = shader_features ?? new HashSet<String>();
                                shader_features.Add( k );
                            }
                        }
                        if ( shader_features != null && multi_compiles != null ) {
                            Debug.AssertFormat( !shader_features.Overlaps( multi_compiles ),
                                String.Format( "shader_features.Overlaps( multi_compiles ) == false, {0}", combinations.shader.name ) );
                        }
                    }
                }
            }
            public bool IsShaderFeatureKeyword( String shaderKeyword ) {
                return shader_features != null && shader_features.Contains( shaderKeyword );
            }
            public bool IsMultiCompileKeyword( String shaderKeyword ) {
                return multi_compiles != null && multi_compiles.Contains( shaderKeyword );
            }
            public bool IsBuiltinKeyword( String shaderKeyword ) {
                return builtins != null && builtins.Contains( shaderKeyword );
            }
        }

        public struct CheckKeywordRecord {
            /// <summary>
            /// IList<ShaderCompilerData> 索引
            /// </summary>
            public int srcDataIndex;
            /// <summary>
            /// 用户收集的一个shader的所有变体中的其中一个变体id，索引+1
            /// </summary>
            public int rawVariantsId;
            /// <summary>
            /// 变体是否存在并经过编译了
            /// </summary>
            public bool exists;
        }

        static Dictionary<String, ShaderVariantInfo> s_ShaderVariantCollections = new Dictionary<String, ShaderVariantInfo>();

        public int callbackOrder {
            get { return 1; }
        }

        static List<ShaderCompilerData> backupShaderCompilerData = new List<ShaderCompilerData>();

        static Shader lastProcessShader = null;
        static ShaderVariantInfo lastProcessShaderVariantInfo = null;

        public static void Reset() {
            // TODO: 注册到构建AssetBundle流程中，监听每一批Bundle构建构建开始结束事件
            // OnBeginBuildAssetBundles
            // OnEndBuildAssetBundles
        }

        static void StableSort<T>( IList<T> list, Comparison<T> comparison = null ) where T : IComparable<T> {
            int count = list.Count;
            for ( int j = 1; j < count; j++ ) {
                T key = list[ j ];
                int i = j - 1;
                for ( ; i >= 0 && ( ( comparison != null && comparison( list[ i ], key ) > 0 ) || ( list[ i ].CompareTo( key ) > 0 ) ); i-- ) {
                    list[ i + 1 ] = list[ i ];
                }
                list[ i + 1 ] = key;
            }
        }

        /// <summary>
        /// 开始构建一批AssetBundle
        /// </summary>
        /// <param name="outpath"></param>
        /// <param name="builds"></param>
        internal static void OnBeginBuildAssetBundles( String outpath, AssetBundleBuild[] builds ) {
            var sb = new StringBuilder();
            for ( int i = 0; i < builds.Length; ++i ) {
                var build = builds[ i ];
                sb.AppendFormat( "ab: {0}", build.assetBundleName ).AppendLine();
                if ( build.assetNames != null ) {
                    for ( int j = 0; j < build.assetNames.Length; ++j ) {
                        sb.AppendFormat( "    asset: {0}", build.assetNames[ j ] ).AppendLine();
                    }
                }
            }
            Debug.LogFormat( "BeginBuildAssetBundles: {0}\t{1}", outpath, sb.ToString() );
        }

        /// <summary>
        /// 一批AssetBundle完成构建，可以认为包中的Shader变体已经被构建完毕，可以做一些校验工作之类
        /// </summary>
        /// <param name="outpath"></param>
        /// <param name="builds"></param>
        /// <param name="manifest"></param>
        internal static void OnEndBuildAssetBundles( String outpath, AssetBundleBuild[] builds, AssetBundleManifest manifest ) {
            CheckLastShaderVariantCompleteness();
            s_ShaderVariantCollections.Clear();
            lastProcessShader = null;
            lastProcessShaderVariantInfo = null;
            backupShaderCompilerData.Clear();
        }

        static void CheckLastShaderVariantCompleteness() {
            if ( lastProcessShader != null && lastProcessShaderVariantInfo != null ) {
                List<ShaderVariantCollectionHelper.ShaderVariant> lastRawVariants;
                if ( lastProcessShaderVariantInfo.rawVariantCollection.TryGetValue( lastProcessShader, out lastRawVariants ) ) {
                }
            }
        }

        public static ShaderVariantInfo LoadShaderVariantInfo( Shader shader, Dictionary<String, ShaderVariantInfo> shaderVariantCollections = null, bool withLog = true ) {
            var shaderPath = AssetDatabase.GetAssetPath( shader );
            if ( String.IsNullOrEmpty( shaderPath ) || EditorUtils.IsUnityDefaultResource( shaderPath ) ) {
                return null;
            }
            ShaderVariantInfo svcInfo = null;
            if ( shaderVariantCollections == null || !shaderVariantCollections.TryGetValue( shaderPath, out svcInfo ) ) {
                var shaderVariantsPath = System.IO.Path.ChangeExtension( shaderPath, ".shadervariants" );
                shaderVariantsPath = shaderVariantsPath.Insert( 0, "Assets/ShaderVariants/" );
                var svc = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>( shaderVariantsPath );
                if ( svc != null ) {
                    var comb = ShaderUtils.ParseShaderCombinations( shader, true, withLog: withLog );
                    if ( comb != null && comb.snippets != null && comb.snippets.Count > 0 ) {
                        svcInfo = new ShaderVariantInfo();
                        svcInfo.variantCollection = svc;
                        svcInfo.rawVariantCollection = ShaderVariantCollectionHelper.ExtractData( svc );
                        svcInfo.combinations = comb;
                        svcInfo.MergeAllShaderKeywords();
                        if ( shaderVariantCollections != null ) {
                            shaderVariantCollections.Add( shaderPath, svcInfo );
                        }
                    }
                }
            }
            return svcInfo;
        }

        public void OnProcessShader( Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data ) {
            if ( snippet.passType == PassType.ForwardAdd ||
                snippet.passType == PassType.LightPrePassBase ||
                snippet.passType == PassType.LightPrePassFinal ||
                snippet.passType == PassType.Deferred ||
                snippet.passType == PassType.ScriptableRenderPipeline ||
                snippet.passType == PassType.ScriptableRenderPipelineDefaultUnlit ||
                snippet.passType == PassType.Meta ||
                snippet.passType == PassType.MotionVectors ) {
                data.Clear();
                return;
            }
            var shaderPath = AssetDatabase.GetAssetPath( shader );
            if ( EditorUtils.IsUnityDefaultResource( shaderPath ) ) {
                return;
            }
            var checkPass = false;
            var shaderData = ShaderUtil.GetShaderData( shader );
            for ( int k = 0; k < shaderData.SubshaderCount; ++k ) {
                var subShader = shaderData.GetSubshader( k );
                for ( int i = 0; i < subShader.PassCount; ++i ) {
                    var pass = subShader.GetPass( i );
                    if ( String.Equals( pass.Name, snippet.passName ) ) {
                        checkPass = true;
                        goto END_PASSCHECK;
                    }
                }
            }
        END_PASSCHECK:
            if ( !checkPass ) {
                // snippet 指的是一段可以被编译Shader代码
                // 传入的shader必然含有可编译代码，如果在当前Shader中未找到对应pass，跳过...
                Debug.AssertFormat( false, String.Format( "snippet '{0}' is invalid for shader: {1}", snippet.passName, shader.name ) );
                return;
            }
            // Unity在编译Shader时，会编译出有效三倍变体量，在AB中，代码相同的可能Unity实现了复用机制来减少内存占用
            // 自带三档硬件等级适配：hw_tier00， hw_tier02， hw_tier01
            // 所以，我们收集到的变体个数 * 3 * pow( 2, multi_compile_num )
            // 在编译一个Shader时，会分类型分别多次调用编译管道，比如VS, PS, GS等等
            // 一个pass含有多个snippet

            // 我们收集到的变体只知道归属的PassType
            backupShaderCompilerData.Clear();
            backupShaderCompilerData.AddRange( data );
            var svcInfo = LoadShaderVariantInfo( shader, s_ShaderVariantCollections );
            if ( svcInfo != null ) {
                List<ShaderVariantCollectionHelper.ShaderVariant> rawVariants;
                if ( !svcInfo.rawVariantCollection.TryGetValue( shader, out rawVariants ) ) {
                    return;
                }
                if ( lastProcessShader != shader ) {
                    CheckLastShaderVariantCompleteness();
                    lastProcessShader = shader;
                    lastProcessShaderVariantInfo = svcInfo;
                }
                var removeCount = 0;
                StringBuilder infoSB = null;
                var full_keywords = new List<String>();
                var feature_keywords = new HashSet<String>();

                // 通常这里拿到的是自定义multi_compiles，而builtin不在此列
                // 我们在Shader中通过编译器开关: multi_compile_fwdbase来引入builtin变体编译
                // 从字面上看multi_compile_fwdbase是属于multi_compiles，但实际上Unity有专门的优化
                // 从ShaderCompilerData中拿到的每一个ShaderKeyword是可以拿到ShaderKeywordType的
                // 有了精准的Keyword类型，实际上需要对Builtin类的Keywords再次进行归类：
                // 举一个简单例子： DIRECTIONAL LIGHTPROBE_SH VERTEXLIGHT_ON 这些是属于BuiltinDefault
                // 有些高级渲染效果属于BuiltinAutoStripped，这种是被Unity自动优化的keyword类型
                // 我认为可以对常用的Builtin Keywords可以也规划到multi_compiles中，这样可能会引入更多的变体
                // 但是光照效果适配会更多更安全，不会太激进

                // 在这里我们没做任何针对Builtin类型的Keyword做过多安全性保留，所以编译裁剪非常激进，安全性较低
                var multi_compiles = svcInfo.multi_compiles;

                // multi_compile_fwdbase:
                // DIRECTIONAL
                // DIRLIGHTMAP_COMBINED
                // DYNAMICLIGHTMAP_ON
                // LIGHTMAP_ON
                // LIGHTMAP_SHADOW_MIXING
                // LIGHTPROBE_SH
                // SHADOWS_SCREEN
                // SHADOWS_SHADOWMASK
                // VERTEXLIGHT_ON

                backupShaderCompilerData.RemoveAll(
                    _data => {
                        feature_keywords.Clear();
                        full_keywords.Clear();
                        var _keywords = _data.shaderKeywordSet.GetShaderKeywords();
                        // 只剔除有关键字的情形，减少代码复杂度
                        // 实际上，无关键字的变体也可能被丢弃不用，简单舍弃这次剔除操作并不会增加太多编译负担
                        if ( _keywords.Length > 0 ) {
                            for ( int j = 0; j < _keywords.Length; ++j ) {
                                var name = _keywords[ j ].GetKeywordName();
                                full_keywords.Add( name );
                                // 如果有当前关键词属于multi_compiles，注意排除
                                if ( multi_compiles != null && multi_compiles.Contains( name ) ) {
                                    // 排除multi_compiles编译宏，这些是必须使用的，不能剔除
                                    continue;
                                }
                                feature_keywords.Add( name );
                            }
                            if ( feature_keywords.Count == 0 ) {
                                return false;
                            }
                            var matched = false;
                            // 遍历所有从项目中搜集到的变体
                            for ( int n = 0; n < rawVariants.Count; ++n ) {
                                var variant = rawVariants[ n ];
                                if ( feature_keywords.Count > variant.keywords.Length ) {
                                    // feature_keywords 已经去掉了multi_compiles，而variant.keywords可能含有
                                    // 从数量的差异就能提前能知道不可能匹配得上
                                    continue;
                                }
                                var matchCount = -1;
                                var mismatchCount = 0;
                                var skipCount = 0;
                                if ( variant.shader == shader && variant.passType == snippet.passType ) {
                                    matchCount = 0;
                                    for ( var m = 0; m < variant.keywords.Length; ++m ) {
                                        var keyword = variant.keywords[ m ];
                                        if ( multi_compiles == null || !multi_compiles.Contains( keyword ) ) {
                                            // 不是multi_compiles的keyword
                                            if ( feature_keywords.Contains( keyword ) ) {
                                                ++matchCount;
                                            } else {
                                                ++mismatchCount;
                                                break;
                                            }
                                        } else {
                                            // 查找匹配的变体时，需要排除multi_compiles关键字
                                            ++skipCount;
                                        }
                                    }
                                }
                                if ( matchCount >= 0 && mismatchCount == 0 &&
                                    matchCount == feature_keywords.Count &&
                                    matchCount + skipCount == variant.keywords.Length ) {
                                    matched = true;
                                    break;
                                }
                            }
                            if ( !matched ) {
                                ++removeCount;
                                // 把删除的先存这里，出错了回重新拿回来
                                data.Add( _data );
                                infoSB = infoSB ?? new StringBuilder();
                                var _fullKeywords = String.Join( " ", full_keywords.ToArray() );
                                infoSB.Append( _fullKeywords ).AppendLine();
                                var info = String.Format( "{0}\nRemove Shader ShaderVariant: {1}", shader.name, _fullKeywords );
                                Debug.Log( info );
                                return true;
                            }
                        }
                        return false;
                    }
                );
                data.Clear();
                backupShaderCompilerData.ForEach( e => data.Add( e ) );
                backupShaderCompilerData.Clear();
                if ( removeCount > 0 && infoSB != null ) {
                    var info = String.Format(
                        "{0}: {1}-'{2}', {3}, count = {4}\nRemove ShaderVariant Count: {5}\n{6}",
                        shader.name, snippet.shaderType, snippet.passName, snippet.passType, data.Count,
                        removeCount, infoSB.ToString()
                    );
                    Debug.Log( info );
                    
                }
            }
        }
    }
}
//EOF
