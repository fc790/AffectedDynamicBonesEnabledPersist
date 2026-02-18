// KKPE_AffectedBonesSnapshot.cs
// v1.1.0
// .NET 3.5 + BepInEx 5 + Old 0Harmony (PatchAny via reflection)
// 功能：F9 快照；之后在“读入角色卡 / 读入服装卡 / 替换角色 / 替换坐标”等事件后自动恢复；取消 F10

using System;
using System.Text;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;

using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FC790s
{
    [BepInPlugin(GUID, NAME, VER)]
    public class KKPE_AffectedBonesSnapshot : BaseUnityPlugin
    {
        public const string GUID = "fc790s.kkpe_affectedbones_snapshot";
        public const string NAME = "KKPE Affected DynamicBones Snapshot (F9 AutoRestore)";
        public const string VER  = "1.1.0";

        private static KKPE_AffectedBonesSnapshot _inst;

        private Harmony _harmony;

        private ConfigEntry<bool> _enable;
        private ConfigEntry<bool> _log;

        // F9 快照后，是否开启自动恢复模式
        private ConfigEntry<bool> _autoRestoreAfterF9;

        // 自动恢复触发点（建议都开）
        private ConfigEntry<bool> _hook_OnCharaLoad;
        private ConfigEntry<bool> _hook_OnLoadClothesFile;
        private ConfigEntry<bool> _hook_OnCharacterReplace;
        private ConfigEntry<bool> _hook_OnCoordinateReplace;

        // 恢复延迟帧（避免刚加载完结构还没完全重建）
        private ConfigEntry<int> _restoreDelayFrames;

        private ConfigEntry<string> _targetNameFilter;

        // NEW: 新出现的 bone 默认 OFF（ignore=true）
        private ConfigEntry<bool> _unknownBonesDefaultOff;

        // ===== snapshot: PoseKey -> ColliderKey -> IgnoredBoneKeys(set)
        private static Dictionary<string, Dictionary<string, HashSet<string>>> S_SnapIgnored =
            new Dictionary<string, Dictionary<string, HashSet<string>>>();

        // ===== snapshot: PoseKey -> SeenBoneKeys(set)
        private static Dictionary<string, HashSet<string>> S_SnapSeen =
            new Dictionary<string, HashSet<string>>();

        private static bool S_HasSnap = false;
        private static bool S_AutoRestoreArmed = false; // F9 后 armed

        // ===== Harmony PatchAny via reflection =====
        private static MethodInfo _miHarmonyPatchAny;

        // ===== reflection cache: KKPE types/fields/methods =====
        private static bool S_RefOk = false;

        private static Type T_PoseController;
        private static Type T_CollidersEditor;
        private static Type T_ColliderDataBase;
        private static Type T_DynamicBonesEditor;
        private static Type T_CharaPoseController;
        private static Type T_BoobsEditor;
        private static Type T_MainWindow;

        private static FieldInfo F_PoseControllers;       // PoseController._poseControllers (static)
        private static FieldInfo F_Pose_CollidersEditor;  // PoseController._collidersEditor
        private static FieldInfo F_Pose_TargetField;      // PoseController._target (field) fallback
        private static FieldInfo F_CE_DirtyColliders;     // CollidersEditor._dirtyColliders
        private static FieldInfo F_CE_Colliders;          // CollidersEditor._colliders (Transform->DynamicBoneCollider)
        private static FieldInfo F_CE_AddNewAsDefault;     // CollidersEditor._addNewDynamicBonesAsDefault (bool)
        private static PropertyInfo P_Pose_TargetProp;    // PoseController.target (prop)

        private static MethodInfo M_SetIgnoreDynamicBone; // CollidersEditor private SetIgnoreDynamicBone(..., bool)
        private static MethodInfo M_ColliderEditor_ResetAll;

        // Bones list
        private static FieldInfo F_Pose_DynBonesEditor;   // PoseController._dynamicBonesEditor
        private static FieldInfo F_DBE_DynamicBones;      // DynamicBonesEditor._dynamicBones

        private static FieldInfo F_CPC_BoobsEditor;       // CharaPoseController._boobsEditor
        private static FieldInfo F_BE_DynamicBones;       // BoobsEditor._dynamicBones

        // ===== MainWindow._self (for optional use) =====
        private static FieldInfo F_MainWindow_Self;

        private void Awake()
        {
            _inst = this;

            _enable = Config.Bind("General", "Enable", true, "Enable this plugin");
            _log = Config.Bind("General", "Log", true, "Log actions");

            _autoRestoreAfterF9 = Config.Bind("AutoRestore", "AutoRestoreAfterF9", true, "After F9 snapshot, auto restore on load events.");

            _hook_OnCharaLoad = Config.Bind("AutoRestore", "Hook_OnCharaLoad", true, "Auto restore after MainWindow.OnCharaLoad");
            _hook_OnLoadClothesFile = Config.Bind("AutoRestore", "Hook_OnLoadClothesFile", true, "Auto restore after MainWindow.OnLoadClothesFile");
            _hook_OnCharacterReplace = Config.Bind("AutoRestore", "Hook_OnCharacterReplace", true, "Auto restore after MainWindow.OnCharacterReplace");
            _hook_OnCoordinateReplace = Config.Bind("AutoRestore", "Hook_OnCoordinateReplace", true, "Auto restore after MainWindow.OnCoordinateReplace");

            _targetNameFilter = Config.Bind("General", "TargetNameFilter", "Collider", 
                "Only affect PoseControllers whose name contains this string. Leave empty or donotfilt to affect all.");

            _restoreDelayFrames = Config.Bind("AutoRestore", "RestoreDelayFrames", 30, "Delay frames before restore after load events (10~30).");

            _unknownBonesDefaultOff = Config.Bind("Restore", "UnknownBonesDefaultOff", true, "Bones not present during F9 will default OFF (ignored) on restore.");

            _harmony = new Harmony(GUID);

            ResolveHarmonyPatchMethodAny();
            TryInitReflection();

            Patch_MainWindow_LoadEvents();
            Patch_StudioScene_Load();
        }

        private IEnumerator SnapshotLater(int frames)
        {
            int i = 0;
            while (i < frames)
            {
                i++;
                yield return null;
            }
        
            DoSnapshotAll();               // 等价 F9
            if (_autoRestoreAfterF9.Value) // 你已有这个配置
                S_AutoRestoreArmed = true; // 之后读卡自动恢复仍然有效
        }

        private static void SceneInfo_Load_Postfix(string _path, ref Version _dataVersion, bool __result)
        {
            try
            {
                if (!__result) return;
                if (_inst == null) return;
                if (!_inst._enable.Value) return;
        
                // 场景加载完成后，延迟 N 帧再 F9（你现在需要 30 才稳，直接复用 restoreDelay）
                int frames = 30;
                try { frames = _inst._restoreDelayFrames.Value; } catch { frames = 30; }
                if (frames < 0) frames = 0;
                if (frames > 600) frames = 600;
        
                _inst.LogI("[AUTO] Scene loaded => auto snapshot (F9) in " + frames + " frames");
                _inst.StartCoroutine(_inst.SnapshotLater(frames));
            }
            catch { }
        }

        private void Patch_StudioScene_Load()
        {
            if (_miHarmonyPatchAny == null) return;
        
            try
            {
                // Studio.SceneInfo.Load(string, out Version) : bool
                MethodInfo miLoad = AccessTools.Method("Studio.SceneInfo:Load",
                    new Type[] { typeof(string), typeof(Version).MakeByRefType() }, null);
        
                if (miLoad == null)
                {
                    LogW("[PATCH] Studio.SceneInfo.Load not found.");
                    return;
                }
        
                MethodInfo post = typeof(KKPE_AffectedBonesSnapshot).GetMethod(
                    "SceneInfo_Load_Postfix",
                    BindingFlags.Static | BindingFlags.NonPublic);
        
                if (post == null)
                {
                    LogW("[PATCH] SceneInfo_Load_Postfix not found.");
                    return;
                }
        
                PatchPostfix(miLoad, post, Priority.Last);
                LogI("[PATCH] Patched Studio.SceneInfo.Load postfix.");
            }
            catch (Exception e)
            {
                LogW("[PATCH] Patch_StudioScene_Load failed: " + e.Message);
            }
        }

        private void Update()
        {
            //if (!_enable.Value) return;
            if (!S_RefOk) return;

            // F9 快照
            if (Input.GetKeyDown(KeyCode.F9))
            {
                DoSnapshotAll();
                if (_autoRestoreAfterF9.Value) S_AutoRestoreArmed = true;
            }

            // F10 取消：不再响应
        }

        // =========================================================
        // Harmony PatchAny
        // =========================================================

        private void ResolveHarmonyPatchMethodAny()
        {
            try
            {
                MethodInfo[] ms = typeof(Harmony).GetMethods(BindingFlags.Instance | BindingFlags.Public);
                for (int i = 0; i < ms.Length; i++)
                {
                    MethodInfo m = ms[i];
                    if (m == null) continue;
                    if (m.Name != "Patch") continue;

                    ParameterInfo[] ps = m.GetParameters();
                    if (ps == null || ps.Length < 3) continue;
                    if (ps[0].ParameterType != typeof(MethodBase)) continue;

                    bool ok = true;
                    for (int k = 1; k < ps.Length; k++)
                    {
                        if (ps[k].ParameterType != typeof(HarmonyMethod)) { ok = false; break; }
                    }
                    if (!ok) continue;

                    _miHarmonyPatchAny = m;
                    LogI("[INIT] Harmony PatchAny resolved: " + m.ToString());
                    return;
                }
                LogW("[INIT] Harmony.Patch(MethodBase, HarmonyMethod...) overload not found.");
            }
            catch (Exception e)
            {
                LogW("[INIT] ResolveHarmonyPatchMethodAny failed: " + e.Message);
            }
        }

        private void PatchPostfix(MethodInfo target, MethodInfo postfix, int priority)
        {
            if (_harmony == null || target == null || postfix == null) return;
            if (_miHarmonyPatchAny == null) return;

            HarmonyMethod hmPost = new HarmonyMethod(postfix);
            hmPost.priority = priority;

            ParameterInfo[] ps = _miHarmonyPatchAny.GetParameters();
            object[] args = new object[ps.Length];

            args[0] = target;
            if (args.Length >= 2) args[1] = null;
            if (args.Length >= 3) args[2] = hmPost;
            for (int i = 3; i < args.Length; i++) args[i] = null;

            try { _miHarmonyPatchAny.Invoke(_harmony, args); }
            catch (Exception e) { LogW("[PATCH] PatchPostfix invoke failed: " + e.Message); }
        }

        // =========================================================
        // Patch MainWindow load events
        // =========================================================

        private void Patch_MainWindow_LoadEvents()
        {
            if (!S_RefOk) { LogW("[PATCH] Reflection not ready, skip MainWindow patches."); return; }
            if (T_MainWindow == null) { LogW("[PATCH] MainWindow type not found."); return; }
            if (_miHarmonyPatchAny == null) { LogW("[PATCH] PatchAny missing, skip MainWindow patches."); return; }

            try
            {
                // 1) OnCharaLoad
                PatchMW("OnCharaLoad", "MW_OnCharaLoad_Postfix");

                // 2) OnLoadClothesFile
                PatchMW("OnLoadClothesFile", "MW_OnLoadClothesFile_Postfix");

                // 3) OnCharacterReplace
                PatchMW("OnCharacterReplace", "MW_OnCharacterReplace_Postfix");

                // 4) OnCoordinateReplace
                PatchMW("OnCoordinateReplace", "MW_OnCoordinateReplace_Postfix");
            }
            catch (Exception e)
            {
                LogW("[PATCH] Patch_MainWindow_LoadEvents failed: " + e.Message);
            }
        }

        private void PatchMW(string methodName, string postfixName)
        {
            MethodInfo mi = T_MainWindow.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (mi == null)
            {
                LogW("[PATCH] MainWindow." + methodName + " not found (skip).");
                return;
            }

            MethodInfo post = typeof(KKPE_AffectedBonesSnapshot).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic);
            if (post == null)
            {
                LogW("[PATCH] Postfix not found: " + postfixName);
                return;
            }

            PatchPostfix(mi, post, Priority.Last);
            LogI("[PATCH] Patched MainWindow." + methodName + " postfix.");
        }

        private static void ArmRestore(string tag)
        {
            try
            {
                if (_inst == null) return;
                if (!_inst._enable.Value) return;
                if (!S_RefOk) return;
                if (!S_HasSnap) return;
                if (!S_AutoRestoreArmed) return; // 必须先 F9

                int frames = 2;
                try { frames = _inst._restoreDelayFrames.Value; } catch { frames = 2; }
                if (frames < 0) frames = 0;
                if (frames > 600) frames = 600;

                _inst.LogI("[AUTO] Triggered by " + tag + " => restore in " + frames + " frames");
                _inst.StartCoroutine(_inst.RestoreLater(frames));
            }
            catch { }
        }

        private static void MW_OnCharaLoad_Postfix()
        {
            if (_inst == null) return;
            if (!_inst._hook_OnCharaLoad.Value) return;
            ArmRestore("OnCharaLoad");
        }

        private static void MW_OnLoadClothesFile_Postfix()
        {
            if (_inst == null) return;
            if (!_inst._hook_OnLoadClothesFile.Value) return;
            ArmRestore("OnLoadClothesFile");
        }

        private static void MW_OnCharacterReplace_Postfix()
        {
            if (_inst == null) return;
            if (!_inst._hook_OnCharacterReplace.Value) return;
            ArmRestore("OnCharacterReplace");
        }

        private static void MW_OnCoordinateReplace_Postfix()
        {
            if (_inst == null) return;
            if (!_inst._hook_OnCoordinateReplace.Value) return;
            ArmRestore("OnCoordinateReplace");
        }

        private IEnumerator RestoreLater(int frames)
        {
            int i = 0;
            while (i < frames)
            {
                i++;
                yield return null;
            }

            DoRestoreAll("AUTO");
        }

        // =========================================================
        // Reflection init (KKPE)
        // =========================================================

        private void TryInitReflection()
        {
            if (S_RefOk) return;

            try
            {
                T_PoseController = FindType("HSPE.PoseController");
                T_CollidersEditor = FindType("HSPE.AMModules.CollidersEditor");
                T_ColliderDataBase = FindType("HSPE.AMModules.CollidersEditor+ColliderDataBase");
                T_DynamicBonesEditor = FindType("HSPE.AMModules.DynamicBonesEditor");
                T_CharaPoseController = FindType("HSPE.CharaPoseController");
                T_BoobsEditor = FindType("HSPE.AMModules.BoobsEditor");
                T_MainWindow = FindType("HSPE.MainWindow");

                if (T_PoseController == null || T_CollidersEditor == null || T_ColliderDataBase == null)
                {
                    LogW("[REF] core types missing.");
                    return;
                }
                F_CE_AddNewAsDefault= T_CollidersEditor.GetField("_addNewDynamicBonesAsDefault", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if(F_CE_AddNewAsDefault == null) LogW("[REF] missing F_CE_AddNewAsDefault.");
                F_PoseControllers = T_PoseController.GetField("_poseControllers", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if(F_PoseControllers == null) LogW("[REF] missing F_PoseControllers.");
                F_Pose_CollidersEditor = T_PoseController.GetField("_collidersEditor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if( F_Pose_CollidersEditor == null) LogW("[REF] missing F_Pose_CollidersEditor.");

                P_Pose_TargetProp = T_PoseController.GetProperty("target", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if(P_Pose_TargetProp == null) LogW("[REF] missing P_Pose_TargetProp.");
                F_Pose_TargetField = T_PoseController.GetField("_target", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if(F_Pose_TargetField == null) LogW("[REF] missing F_Pose_TargetField.");

                F_CE_DirtyColliders = T_CollidersEditor.GetField("_dirtyColliders", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if( F_CE_DirtyColliders == null) LogW("[REF] missing F_CE_DirtyColliders.");
                F_CE_Colliders = T_CollidersEditor.GetField("_colliders", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if(F_CE_Colliders == null) LogW("[REF] missing F_CE_Colliders.");

                MethodInfo[] ms = T_CollidersEditor.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < ms.Length; i++)
                {
                    MethodInfo m = ms[i];
                    if (m == null) continue;
                    if (m.Name != "SetIgnoreDynamicBone" && m.Name != "ResetAll") continue;
                    if (m.Name == "ResetAll")
                    {
                        M_ColliderEditor_ResetAll = m;
                        continue;
                    }
                    if (m.Name == "SetIgnoreDynamicBone")
                    {
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps == null) continue;
                        if (ps.Length != 4) continue;
                        if (ps[3].ParameterType != typeof(bool)) continue;
                        M_SetIgnoreDynamicBone = m;
                        break;
                    }
                    else{
                        LogW("[REF] missing M_SetIgnoreDynamicBone");
                    }                   
                }

                F_Pose_DynBonesEditor = T_PoseController.GetField("_dynamicBonesEditor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (T_DynamicBonesEditor != null)
                    F_DBE_DynamicBones = T_DynamicBonesEditor.GetField("_dynamicBones", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                else{
                    LogW("[REF] missing F_DBE_DynamicBones");
                }
                if (T_CharaPoseController != null)
                    F_CPC_BoobsEditor = T_CharaPoseController.GetField("_boobsEditor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                else{
                    LogW("[REF] missing F_CPC_BoobsEditor");
                }
                if (T_BoobsEditor != null)
                    F_BE_DynamicBones = T_BoobsEditor.GetField("_dynamicBones", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                else{
                    LogW("[REF] missing F_BE_DynamicBones");
                }
                if (T_MainWindow != null)
                {
                    F_MainWindow_Self = T_MainWindow.GetField("_self", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }
                else{
                    LogW("[REF] missing F_MainWindow_Self");
                }

                if (F_PoseControllers == null || F_Pose_CollidersEditor == null ||
                    F_CE_DirtyColliders == null || F_CE_Colliders == null ||
                    M_SetIgnoreDynamicBone == null)
                {
                    LogW("[REF] missing members.");
                    return;
                }

                S_RefOk = true;
                LogI("[REF] OK");
            }
            catch (Exception e)
            {
                LogW("[REF] Exception: " + e.Message);
            }
        }

        private static Type FindType(string fullName)
        {
            Type t = Type.GetType(fullName, false);
            if (t != null) return t;

            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                Assembly a = asms[i];
                if (a == null) continue;
                try
                {
                    t = a.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        // =========================================================
        // PoseControllers list
        // =========================================================

        private List<object> GetPoseControllersList()
        {
            List<object> list = new List<object>();

            try
            {
                if (F_PoseControllers != null)
                {
                    object obj = F_PoseControllers.GetValue(null);
                    if (obj != null)
                    {
                        IEnumerable en = obj as IEnumerable;
                        if (en != null)
                        {
                            IEnumerator it = en.GetEnumerator();
                            while (it.MoveNext())
                            {
                                object pc = it.Current;
                                if (pc != null) list.Add(pc);
                            }
                            if (list.Count > 0) return list;
                        }
                    }
                }
            }
            catch { }

            try
            {
                if (T_PoseController != null)
                {
                    UnityEngine.Object[] arr = Resources.FindObjectsOfTypeAll(T_PoseController);
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Length; i++)
                        {
                            if (arr[i] != null) list.Add(arr[i]);
                        }
                    }
                }
            }
            catch { }

            return list;
        }

        private object GetCollidersEditor(object poseController)
        {
            if (poseController == null) return null;
            if (F_Pose_CollidersEditor == null) return null;
            return F_Pose_CollidersEditor.GetValue(poseController);
        }

        // =========================================================
        // F9 snapshot
        // =========================================================

        private void DoSnapshotAll()
        {
            List<object> poses = GetPoseControllersList();
            if (poses == null || poses.Count == 0)
            {
                LogW("[F9] PoseControllers list empty/null.");
                return;
            }

            Dictionary<string, Dictionary<string, HashSet<string>>> snapIgnored =
                new Dictionary<string, Dictionary<string, HashSet<string>>>();
            Dictionary<string, HashSet<string>> snapSeen =
                new Dictionary<string, HashSet<string>>();

            for (int i = 0; i < poses.Count; i++)
            {
                object pose = poses[i];
                if (pose == null) continue;

                string poseKey = BuildPoseKey(pose);
                if (string.IsNullOrEmpty(poseKey)) continue;
                string filter = _targetNameFilter.Value;
                if(_log.Value)
                {
                    
                    if (poseKey.Contains(filter))
                    {
                        LogI("snap find J694: "+poseKey);
                    }
                    else
                    {
                        LogI("snap posekey is: "+poseKey);
                    }
                }
                if (filter != "donotfilt"){
                    if (!poseKey.Contains(filter)) 
                    {continue;}
                }
                // Seen bones
                List<object> seenBones = BuildBoneListForPose(pose);
                HashSet<string> seenKeys = new HashSet<string>();
                if (seenBones != null)
                {
                    for (int bi = 0; bi < seenBones.Count; bi++)
                    {
                        object b = seenBones[bi];
                        if (b == null) continue;
                        string bk = BuildBoneKey(pose, b);
                        if (!string.IsNullOrEmpty(bk) && !seenKeys.Contains(bk)) seenKeys.Add(bk);
                    }
                }
                if (!snapSeen.ContainsKey(poseKey)) snapSeen.Add(poseKey, seenKeys);

                object ce = GetCollidersEditor(pose);
                if (ce == null) continue;

                // 改：不再依赖 _dirtyColliders，而是遍历 _colliders 全量 collider
                IDictionary colliders = (F_CE_Colliders != null) ? (F_CE_Colliders.GetValue(ce) as IDictionary) : null;
                if (colliders == null) continue;
                
                Dictionary<string, HashSet<string>> poseEntry = new Dictionary<string, HashSet<string>>();
                
                // 对该 pose 的每一个 collider 做快照（即便它从未 dirty 过）
                foreach (DictionaryEntry cde in colliders)
                {
                    object colliderObj = cde.Value;
                    DynamicBoneCollider dbc = colliderObj as DynamicBoneCollider;
                    if (dbc == null) continue;
                
                    string colliderKey = BuildColliderKey(pose, dbc);
                    if (string.IsNullOrEmpty(colliderKey)) continue;
                
                    // 计算 ignoredKeys：对 seenBones 逐个判断它是否包含 dbc
                    HashSet<string> ignoredKeys = new HashSet<string>();
                
                    if (seenBones != null)
                    {
                        for (int bi = 0; bi < seenBones.Count; bi++)
                        {
                            object boneObj = seenBones[bi];
                            if (boneObj == null) continue;
                
                            string boneKey = BuildBoneKey(pose, boneObj);
                            if (string.IsNullOrEmpty(boneKey)) continue;
                
                            // 不包含 collider => ignored
                            if (!BoneHasCollider(boneObj, dbc))
                            {
                                if (!ignoredKeys.Contains(boneKey)) ignoredKeys.Add(boneKey);
                            }
                        }
                    }
                
                    // 关键：允许 ignoredKeys 为空（表示全 ON），但也必须写入 poseEntry
                    if (!poseEntry.ContainsKey(colliderKey))
                        poseEntry.Add(colliderKey, ignoredKeys);
                }
                
                // 关键：poseEntry 即便 collider 全部“全 ON”，也会有条目，所以这里直接写入 snap
                if (poseEntry.Count > 0)
                {
                    if (!snapIgnored.ContainsKey(poseKey))
                        snapIgnored.Add(poseKey, poseEntry);
                }
            }

            S_SnapIgnored = snapIgnored;
            S_SnapSeen = snapSeen;
            S_HasSnap = true;

            // 统计
            int totalColliders = 0;
            foreach (KeyValuePair<string, Dictionary<string, HashSet<string>>> kv in S_SnapIgnored)
            {
                if (kv.Value != null) totalColliders += kv.Value.Count;
            }

            LogI("[F9] Snapshot OK. poses=" + S_SnapIgnored.Count);
            LogI("[F9] Snapshot detail: poses=" + S_SnapIgnored.Count + " colliders=" + totalColliders);

            if (_autoRestoreAfterF9.Value) S_AutoRestoreArmed = true;
        }

        // =========================================================
        // Restore (AUTO only)
        // =========================================================

        private void DoRestoreAll(string tag)
        {
            if (!S_HasSnap || S_SnapIgnored == null || S_SnapIgnored.Count == 0)
            {
                LogW("[" + tag + "] No snapshot.");
                return;
            }

            List<object> poses = GetPoseControllersList();
            if (poses == null || poses.Count == 0)
            {
                LogW("[" + tag + "] PoseControllers list empty/null.");
                return;
            }

            int ok = 0;
            for (int i = 0; i < poses.Count; i++)
            {
                object pose = poses[i];
                if (pose == null) continue;
                if (TryRestoreOnePose(pose)) ok++;
            }

            LogI("[" + tag + "] Restore done. ok=" + ok + "/" + poses.Count);
        }

        private bool TryRestoreOnePose(object pose)
        {
            string poseKey = BuildPoseKey(pose);
            if (string.IsNullOrEmpty(poseKey)) return false;
            string filter = _targetNameFilter.Value;
            if(_log.Value)
            {
                
                if (poseKey.Contains(filter))
                {
                    LogI("find J694: "+poseKey);
                }
                else
                {
                    LogI("posekey is: "+poseKey);
                }
            }
            if (filter != "donotfilt"){
                if (!poseKey.Contains(filter)) 
                {return false;}
            }

            Dictionary<string, HashSet<string>> poseSnap;
            if (!S_SnapIgnored.TryGetValue(poseKey, out poseSnap) || poseSnap == null || poseSnap.Count == 0)
                return false;

            HashSet<string> seenKeys = null;
            S_SnapSeen.TryGetValue(poseKey, out seenKeys);

            object ce = GetCollidersEditor(pose);
            if (ce == null) return false;
            // ---- temporarily disable HSPE "add new bones as default" for this CollidersEditor ----
            bool prevFlag = true;

            if (F_CE_AddNewAsDefault != null)
            {
                try
                {
                    object v = F_CE_AddNewAsDefault.GetValue(ce);
                    if (v is bool)
                    {
                        prevFlag = (bool)v;

                        // force false during restore
                        F_CE_AddNewAsDefault.SetValue(ce, false);

                        LogI("[RESTORE][FLAG] pose=" + poseKey + " _addNewDynamicBonesAsDefault " +
                             prevFlag.ToString() + " -> false (forced)");
                    }
                }
                catch
                {
                    // ignore
                }
            }

            IDictionary colliders = (F_CE_Colliders != null) ? (F_CE_Colliders.GetValue(ce) as IDictionary) : null;
            if (colliders == null) return false;

            List<object> allBones = BuildBoneListForPose(pose);
            if (allBones == null || allBones.Count == 0)
            {
                LogW("[RESTORE] No bones list for pose=" + poseKey);
                return false;
            }

            foreach (DictionaryEntry de in colliders)
            {
                object colliderObj = de.Value;
                if (colliderObj == null) continue;

                string colliderKey = BuildColliderKey(pose, colliderObj);
                if (string.IsNullOrEmpty(colliderKey)) continue;

                HashSet<string> ignoredKeys;
                if (!poseSnap.TryGetValue(colliderKey, out ignoredKeys) || ignoredKeys == null)
                    continue;

                for (int bi = 0; bi < allBones.Count; bi++)
                {
                    object boneObj = allBones[bi];
                    if (boneObj == null) continue;

                    string boneKey = BuildBoneKey(pose, boneObj);
                    if (string.IsNullOrEmpty(boneKey)) continue;

                    bool shouldIgnore = false;

                    if (ignoredKeys.Contains(boneKey))
                    {
                        shouldIgnore = true;
                    }
                    else
                    {
                        if (_unknownBonesDefaultOff.Value && seenKeys != null)
                        {
                            if (!seenKeys.Contains(boneKey))
                                shouldIgnore = true;
                        }
                    }

                    SafeInvoke_SetIgnoreDynamicBone(ce, colliderObj, pose, boneObj, shouldIgnore);
                }
            }

            LogI("[RESTORE] Restored pose=" + poseKey);
            return true;
        }

        private void SafeInvoke_SetIgnoreDynamicBone(object collidersEditor, object colliderObj, object poseController, object boneObj, bool ignore)
        {
            if (M_SetIgnoreDynamicBone == null) return;
            if (collidersEditor == null || colliderObj == null || poseController == null || boneObj == null) return;

            try
            {
                object[] args = new object[4];
                args[0] = colliderObj;
                args[1] = poseController;
                args[2] = boneObj;
                args[3] = ignore;
                M_SetIgnoreDynamicBone.Invoke(collidersEditor, args);
                //M_ColliderEditor_ResetAll.Invoke(collidersEditor,null);
            }
            catch (Exception e)
            {
                LogW("[CALL] SetIgnoreDynamicBone failed: " + e.Message);
            }
        }

        // =========================================================
        // Keys (PoseKey: OCI identity; avoids multi-J694 collision)
        // =========================================================
        // 判断：某个 boneObj 是否“当前包含”这个 collider（即 UI 里是 ON）
        private bool BoneHasCollider(object boneObj, DynamicBoneCollider dbc)
        {
            if (boneObj == null || dbc == null) return false;
        
            // DynamicBone (v1)
            DynamicBone db = boneObj as DynamicBone;
            if (db != null)
            {
                List<DynamicBoneCollider> list = db.m_Colliders;
                return (list != null && list.Contains(dbc));
            }
        
            // DynamicBone_Ver02 (v2)
            Type t = boneObj.GetType();
            if (t != null && t.Name == "DynamicBone_Ver02")
            {
                try
                {
                    // property Colliders : List<DynamicBoneCollider>
                    PropertyInfo p = t.GetProperty("Colliders", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    object v = (p != null) ? p.GetValue(boneObj, null) : null;
                    List<DynamicBoneCollider> list2 = v as List<DynamicBoneCollider>;
                    return (list2 != null && list2.Contains(dbc));
                }
                catch { return false; }
            }
        
            return false;
        }

        private string BuildPoseKey(object poseController)
        {
            try
            {
                object target = null;
                if (P_Pose_TargetProp != null) target = P_Pose_TargetProp.GetValue(poseController, null);
                if (target == null && F_Pose_TargetField != null) target = F_Pose_TargetField.GetValue(poseController);
                if (target == null) goto FALLBACK_GO;

                Type tTarget = target.GetType();
                PropertyInfo pOci = tTarget.GetProperty("oci", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object oci = (pOci != null) ? pOci.GetValue(target, null) : null;
                if (oci == null) goto FALLBACK_GO;

                int ociId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(oci);

                string tn = "";
                try
                {
                    PropertyInfo pGuide = oci.GetType().GetProperty("guideObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    object guide = (pGuide != null) ? pGuide.GetValue(oci, null) : null;
                    if (guide != null)
                    {
                        PropertyInfo pTT = guide.GetType().GetProperty("transformTarget", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        object tt = (pTT != null) ? pTT.GetValue(guide, null) : null;
                        Transform tr = tt as Transform;
                        if (tr != null) tn = tr.name;
                    }
                }
                catch { }

                return "OCI#" + ociId.ToString() + "|TT|" + (tn ?? "");

            FALLBACK_GO:
                Component c = poseController as Component;
                if (c != null) return "GO#" + c.GetInstanceID().ToString() + "|" + c.gameObject.name;
            }
            catch { }

            return null;
        }

        private string BuildColliderKey(object poseController, object colliderObj)
        {
            DynamicBoneCollider dbc = colliderObj as DynamicBoneCollider;
            if (dbc == null) return null;

            Component pc = poseController as Component;
            if (pc == null) return "COL|" + dbc.name;

            string rel = GetRelPath(pc.transform, dbc.transform);
            return "COL|" + rel;
        }

        private string BuildBoneKey(object poseController, object boneObj)
        {
            Component pc = poseController as Component;
            Transform pcRoot = (pc != null) ? pc.transform : null;

            DynamicBone db = boneObj as DynamicBone;
            if (db != null)
            {
                Transform r = db.m_Root;
                if (r == null) return null;
                string rel = (pcRoot != null) ? GetRelPath(pcRoot, r) : r.name;
                return "DB|" + rel;
            }

            Type t = boneObj.GetType();
            if (t != null && t.Name == "DynamicBone_Ver02")
            {
                PropertyInfo pRoot = t.GetProperty("Root", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object ro = (pRoot != null) ? pRoot.GetValue(boneObj, null) : null;
                Transform r2 = ro as Transform;
                if (r2 == null) return null;
                string rel2 = (pcRoot != null) ? GetRelPath(pcRoot, r2) : r2.name;
                return "DB2|" + rel2;
            }

            return null;
        }

        private static string GetRelPath(Transform root, Transform t)
        {
            if (root == null || t == null) return "";
            if (t == root) return root.name;

            List<string> parts = new List<string>();
            Transform cur = t;
            while (cur != null && cur != root)
            {
                parts.Add(cur.name);
                cur = cur.parent;
            }

            if (cur != root)
            {
                parts.Clear();
                cur = t;
                int guard = 0;
                while (cur != null && guard < 32)
                {
                    parts.Add(cur.name);
                    cur = cur.parent;
                    guard++;
                }
            }

            parts.Reverse();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append("/");
                sb.Append(parts[i]);
            }
            return sb.ToString();
        }

        // =========================================================
        // Bones list (includes lone-collider global scan fallback)
        // =========================================================

        private List<object> BuildBoneListForPose(object poseController)
        {
            List<object> list = new List<object>();

            // 1) normal: pose._dynamicBonesEditor._dynamicBones (IEnumerable)
            try
            {
                if (F_Pose_DynBonesEditor != null && F_DBE_DynamicBones != null)
                {
                    object dbe = F_Pose_DynBonesEditor.GetValue(poseController);
                    if (dbe != null)
                    {
                        object dynListObj = F_DBE_DynamicBones.GetValue(dbe);
                        IEnumerable en = dynListObj as IEnumerable;
                        if (en != null)
                        {
                            IEnumerator it = en.GetEnumerator();
                            while (it.MoveNext())
                            {
                                object o = it.Current;
                                if (o != null) list.Add(o);
                            }
                        }
                    }
                }
            }
            catch { }

            // 2) boobs (DynamicBone_Ver02)
            try
            {
                if (T_CharaPoseController != null && T_CharaPoseController.IsInstanceOfType(poseController))
                {
                    if (F_CPC_BoobsEditor != null && F_BE_DynamicBones != null)
                    {
                        object be = F_CPC_BoobsEditor.GetValue(poseController);
                        if (be != null)
                        {
                            object arrObj = F_BE_DynamicBones.GetValue(be);
                            IEnumerable en2 = arrObj as IEnumerable;
                            if (en2 != null)
                            {
                                IEnumerator it2 = en2.GetEnumerator();
                                while (it2.MoveNext())
                                {
                                    object o2 = it2.Current;
                                    if (o2 != null) list.Add(o2);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 3) lone-collider fallback: global scan
            if (list.Count == 0)
            {
                try
                {
                    UnityEngine.Object[] allDB = Resources.FindObjectsOfTypeAll(typeof(DynamicBone));
                    if (allDB != null)
                    {
                        for (int i = 0; i < allDB.Length; i++)
                            if (allDB[i] != null) list.Add(allDB[i]);
                    }
                }
                catch { }

                try
                {
                    Type tDB2 = FindType("DynamicBone_Ver02");
                    if (tDB2 == null) tDB2 = FindType("DynamicBone.DynamicBone_Ver02");
                    if (tDB2 != null)
                    {
                        UnityEngine.Object[] allDB2 = Resources.FindObjectsOfTypeAll(tDB2);
                        if (allDB2 != null)
                        {
                            for (int j = 0; j < allDB2.Length; j++)
                                if (allDB2[j] != null) list.Add(allDB2[j]);
                        }
                    }
                }
                catch { }
            }

            return list;
        }

        // =========================================================
        // Logging
        // =========================================================

        private void LogI(string s) { if (_log.Value) Logger.LogInfo(s); }
        private void LogW(string s) { if (_log.Value) Logger.LogWarning(s); }
    }
}
