## 简单使用步骤说明

1. 在ShaderCollection.cs中，根据实际项目添加shader资源目录，确保执行菜单项"Tools/Shader/Update Shader Collection"能够把项目中所有使用的shader以及配套的svc资源都被引用进来用于ab构建。该prefab保存在"Assets/Resources/Shaders/ShaderCollection.prefab"
2. 在ShaderVariantCollectionExporter.CollectAllMaterialAssetsForGame()方法中，提供整个项目中，非场景关联的材质，可以通过分析项目中的角色、特效等资源的依赖关系得到
3. 在ShaderVariantCollectionExporter.CollectDynamicLightingMaterials()方法中，提供项目中会在各种光照环境下使用的、支持光照、阴影的材质资源、
4. 在ShaderVariantCollectionExporter.ShaderVariantsCollector()方法中，根据项目实际光照、烘焙模式，选择需要支持的光照场景（LightEnvs目录下，保存了几个不同光照烘焙环境，可按需开启）
5. 如果：还有部分效果的材质并没有静态关联到prefab上，有些效果时通过游戏运行时实时切换材质，开启某些keyword的话，那么最好也能够提供这些实时效果变种材质的生成，用于变体收集过程
6. 配置好之后，可以通过"Tools/Shader/Open ShaderVariants Collector"打开收集工具面板，点击开始按钮，进行整个变体收集过程

## 其他
1. 此变体收集工具基于unity2018开发，升级到Unity2019以及之后，unity支持了shader_feature_local，变体关键词上有了global和local的区分，api上有些已经过期，还未对应升级
2. 此变体收集工具主要提供一些收集思路，落实到具体项目中还是需要提供一个完整的资源归类处理的逻辑。对于shader、材质、光照环境这些对象的使用场景需要有清晰的使用场景和划分，才能保证写出来的变体收集器尽可能不会遗漏，估计这也是unity无法提供统一的变体收集器原因吧
