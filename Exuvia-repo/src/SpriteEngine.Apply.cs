// ============================================================================
//  SpriteEngine.Apply.cs — часть класса SpriteEngine: надеть и снять листы, запас готовых сборок.
//  Общее описание — в начале SpriteEngine.cs.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;

namespace Exuvia
{
    internal static partial class SpriteEngine
    {
        // ---- надеть / снять ----------------------------------------------------

        static void ApplyTo(tk2dSpriteCollectionData collection, int id, Build build)
        {
            var applyClock = System.Diagnostics.Stopwatch.StartNew();
            var record = new Applied();
            record.Coll = collection;
            record.Build = build;
            int complex = 0, moved = 0, turned = 0;
            var atlasNames = new Dictionary<int, string>();
            var pos = new P2[4];
            var outPos = new P2[4];
            var outUv = new P2[4];
            var oldUv = new P2[4];

            tk2dSpriteDefinition[] defs = collection.spriteDefinitions;
            for (int i = 0; i < defs.Length; i++)
            {
                tk2dSpriteDefinition def = defs[i];
                if (def == null || string.IsNullOrEmpty(def.name)) continue;
                Material mat = def.materialInst != null ? def.materialInst : def.material;
                if (mat == null) continue;

                // Имя атласа — первое слово имени материала ("atlas0 material" -> "atlas0").
                Material named = def.material != null ? def.material : mat;
                int mid = named.GetInstanceID();
                string atlas;
                if (!atlasNames.TryGetValue(mid, out atlas))
                {
                    atlas = FirstWord(named.name).ToLowerInvariant();
                    atlasNames[mid] = atlas;
                }

                string lname = def.name.ToLowerInvariant();
                Slot slot;
                if (!build.Slots.TryGetValue(atlas + "|" + lname, out slot)
                    && !build.ByName.TryGetValue(lname, out slot)) continue;
                if (!slot.Done) continue;
                if (def.positions == null || def.positions.Length != 4 || def.uvs == null
                    || def.uvs.Length != 4) { complex++; continue; }

                // Размер родного спрайта в пикселях (как он выглядит в игре, то есть уже без поворота в атласе).
                bool rotated = def.flipped != tk2dSpriteDefinition.FlipMode.None;
                int flip = def.flipped == tk2dSpriteDefinition.FlipMode.Tk2d ? 1 : (def.flipped == tk2dSpriteDefinition.FlipMode.TPackerCW
                    ? 2 : 0);
                for (int k = 0; k < 4; k++)
                {
                    pos[k] = new P2(def.positions[k].x, def.positions[k].y);
                    oldUv[k] = new P2(def.uvs[k].x, def.uvs[k].y);
                }
                int sw = 0, sh = 0;
                Texture texture = mat.HasProperty("_MainTex") ? mat.mainTexture : null;
                if (texture != null)
                {
                    int rw, rh;
                    SpriteGeometry.AtlasRect(oldUv, texture.width, texture.height, out rw, out rh);
                    sw = rotated ? rh : rw;
                    sh = rotated ? rw : rh;
                }

                string how;
                bool grow = sw > 0 && sh > 0 && SpriteGeometry.Place(pos, sw, sh, slot.W, slot.H, slot.File.Anchor,
                    slot.File.PivotX, slot.File.PivotY,
                    rotated, outPos, out how);
                if (rotated) turned++;

                Texture2D page = build.Pages[slot.Page];
                float u0 = (float)slot.X / page.width, u1 = (float)(slot.X + slot.W) / page.width;
                float v0 = (float)slot.Y / page.height, v1 = (float)(slot.Y + slot.H) / page.height;
                SpriteGeometry.Uvs(oldUv, flip, u0, v0, u1, v1, outUv);

                var rec = new DefRec();
                rec.Def = def;
                rec.Uvs = def.uvs;
                rec.Positions = def.positions;
                rec.MatInst = def.materialInst;
                rec.Flipped = def.flipped;
                rec.Ours = MaterialFor(build, mat, slot.Page, texture);
                record.Recs.Add(rec);

                var uvs = new Vector2[4];
                for (int k = 0; k < 4; k++) uvs[k] = new Vector2(outUv[k].X, outUv[k].Y);
                def.uvs = uvs;
                if (grow)
                {
                    var np = new Vector3[4];
                    for (int k = 0; k < 4; k++) np[k] = new Vector3(outPos[k].X, outPos[k].Y, def.positions[k].z);
                    def.positions = np;
                    moved++;
                }
                def.flipped = tk2dSpriteDefinition.FlipMode.None; // на нашем листе картинка лежит прямо
                def.materialInst = rec.Ours;
            }

            if (record.Recs.Count == 0) return;
            build.Refs++;
            applied[id] = record;
            touched.Add(id);
            Plugin.Log.LogInfo("Exuvia: " + collection.name + ": подменено спрайтов " + record.Recs.Count + " из "
                + defs.Length
                + (moved > 0 ? ", увеличенных " + moved : "") + (turned > 0 ? ", повёрнутых в атласе " + turned : "")
                    + (complex > 0 ? ", пропущено со сложной формой " + complex : "")
                + ", " + applyClock.Elapsed.TotalMilliseconds.ToString("0.0") + " мс");
        }

        static Material MaterialFor(Build build, Material original, int page, Texture template)
        {
            long key = ((long)original.GetInstanceID() << 8) ^ (uint)page;
            Material material;
            if (build.Mats.TryGetValue(key, out material) && material != null) return material;
            material = new Material(original);
            material.name = original.name;
            material.hideFlags = HideFlags.HideAndDontSave;
            Texture2D tex = build.Pages[page];
            if (template != null)
            {
                tex.filterMode = template.filterMode;
                tex.anisoLevel = template.anisoLevel;
            }
            material.mainTexture = tex;
            build.Mats[key] = material;
            return material;
        }

        /// Цела ли подмена (tk2d мог заново подготовить коллекцию и вернуть свои материалы).
        static bool Intact(Applied record)
        {
            if (record.Recs.Count == 0) return true;
            DefRec first = record.Recs[0];
            DefRec last = record.Recs[record.Recs.Count - 1];
            return first.Def.materialInst == first.Ours && last.Def.materialInst == last.Ours;
        }

        static void Restore(Applied record, int id)
        {
            foreach (DefRec rec in record.Recs)
            {
                rec.Def.uvs = rec.Uvs;
                rec.Def.positions = rec.Positions;
                rec.Def.flipped = rec.Flipped;
                // Если tk2d уже сам вернул свой материал — не мешаем.
                if (rec.Def.materialInst == rec.Ours || rec.Def.materialInst == null)
                    rec.Def.materialInst = rec.MatInst;
            }
            applied.Remove(id);
            touched.Add(id);
            Release(record.Build);
        }

        static void Release(Build build)
        {
            build.Refs--;
        }

        static void Retire(Build build)
        {
            if (retired.Contains(build)) return;
            if (!build.Ready && build.JobsOut > 0)
            {
                // Листы ещё собираются, а сборка уже не нужна: просим фоновые потоки бросить её.
                build.Cancel = true;
                int dropped = DecodeJobs.DropQueued(build).Count;
                build.JobsOut -= dropped;
                jobsOut -= dropped;
            }
            retired.Add(build);
        }

        /// Перерисовываем спрайты изменённых коллекций (до того, как убирать ненужные листы).
        static void RefreshSprites()
        {
            int rebuilt = 0;
            var refreshClock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                tk2dBaseSprite[] sprites = UnityEngine.Object.FindObjectsByType<tk2dBaseSprite>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                for (int i = 0; i < sprites.Length; i++)
                {
                    tk2dBaseSprite sprite = sprites[i];
                    if (sprite == null) continue;
                    tk2dSpriteCollectionData coll = sprite.Collection;
                    if (coll == null) continue;
                    if (!touched.Contains(coll.GetInstanceID())) continue;

                    // Спрайт, который ещё ни разу не строился, построится сам с новыми данными.
                    MeshFilter meshFilter = sprite.GetComponent<MeshFilter>();
                    if (meshFilter == null || meshFilter.sharedMesh == null) continue;
                    try
                    {
                        sprite.ForceBuild();
                        rebuilt++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("Exuvia: не перерисовался спрайт " + sprite.name + ": " + e.Message);
                    }
                }
            }
            finally
            {
                touched.Clear();
            }

            if (rebuilt > 0) Plugin.Log.LogInfo("Exuvia: перерисовано спрайтов на сцене: " + rebuilt + " за "
                + refreshClock.Elapsed.TotalMilliseconds.ToString("0.0") + " мс");
        }

        /// Старые листы, которые уже никто не показывает, убираем из памяти.
        static void Cleanup()
        {
            bool kept = false;
            for (int i = retired.Count - 1; i >= 0; i--)
            {
                Build build = retired[i];
                if (build.Refs > 0) continue;
                if (build.JobsOut > 0) continue; // фоновые потоки ещё пишут в память её листов
                retired.RemoveAt(i);
                if (build.Ready && !build.Cancel && !build.Disposed && build.Key != null && Spare.BudgetMb > 0)
                {
                    // Готовую сборку не выбрасываем: скин (или крест) могут включить снова.
                    Build old;
                    if (spare.TryGetValue(build.Key, out old) && !ReferenceEquals(old, build)) Dispose(old);
                    build.Used = Spare.Next();
                    spare[build.Key] = build;
                    kept = true;
                }
                else Dispose(build);
            }
            if (kept) Spare.Trim();
        }

        // ---- запас готовых сборок (для Spare.cs) ---------------------------------

        internal static long SpareBytes
        {
            get
            {
                long px = 0;
                foreach (Build build in spare.Values) px += build.Pixels;
                return px * 4;
            }
        }

        internal static long OldestSpare
        {
            get
            {
                long oldest = long.MaxValue;
                foreach (Build build in spare.Values) if (build.Used < oldest) oldest = build.Used;
                return oldest;
            }
        }

        internal static bool EvictOldestSpare()
        {
            Build pick = null;
            foreach (Build build in spare.Values) if (pick == null || build.Used < pick.Used) pick = build;
            if (pick == null) return false;
            spare.Remove(pick.Key);
            Dispose(pick);
            return true;
        }

        static void Dispose(Build build)
        {
            if (build.Disposed) return;
            build.Disposed = true;
            foreach (Material material in build.Mats.Values) if (material != null) UnityEngine.Object.Destroy(material);
            foreach (Texture2D texture in build.Pages) if (texture != null) UnityEngine.Object.Destroy(texture);
            build.Mats.Clear();
            build.Pages.Clear();
        }

        static string FirstWord(string s)
        {
            if (s == null) return "";
            int sp = s.IndexOf(' ');
            return sp > 0 ? s.Substring(0, sp) : s;
        }
    }
}
