# 对局模拟角色占位素材

这四张角色图仅供独立 H5 对局模拟器使用，与 Unity 资源和打包无关。

- 来源：OpenAI 内置 `image_gen` 工具（built-in），2026-10-19。
- 每个角色分别生成，无外部图片参考；原始大图保留在本机 Codex 生成目录，不进入仓库。
- 使用 macOS `sips -Z 192` 等比缩小；四张均为 192 × 192 PNG，保留透明通道。
- 已逐张检查小图：完整角色、轮廓清楚、玩家朝右、敌方朝左或向左侧身，透明底无场景。
- PNG 总计 191,629 字节（约 187 KiB）。渲染放大和动作由 H5/CSS 完成，无逐帧序列图。
- 同目录 `icons.svg` 为网页手工矢量图标，不属于这些生成图。

| 文件 | 用途 | 大小 |
| --- | --- | ---: |
| `hero.png` | 玩家扑克冒险家 | 43,407 字节 |
| `grunt.png` | 轮换小怪、召唤护卫占位 | 44,234 字节 |
| `summoner.png` | 护甲召唤精英 | 52,070 字节 |
| `gluttony.png` | 暴食 Boss | 51,918 字节 |

## 最终提示词

每次独立调用均设置 `transparent_background: true`，使用以下完整提示词。

### hero.png

```text
Use case: stylized-concept. Asset type: small character sprite for an independent HTML card-battle simulator. Style: original dark fantasy, hand-painted flat-shaded cartoon, bold charcoal silhouette and clear large shapes, deliberately simple detail readable at 192 pixels, muted ink blue / dusty ochre / warm bone palette. Composition: one single full-body character, ground-level three-quarter side view, all limbs and accessories within frame, centered with only a slim transparent margin. Backdrop: genuinely transparent; no floor, shadow blob, scenery, border, frame, lettering or watermark. Keep image roughly square. Not a copy of any existing game character. Subject: a mysterious human poker adventurer wearing a long navy coat and ochre scarf, face partly shaded by a broad brim hat, holding a small fan of blank playing cards in one hand and a plain short cane in the other. Slim confident silhouette. Faces RIGHT toward an enemy at the center of a horizontal battle scene. Sparse restrained cyan card glimmer only, no text or suit glyphs.
```

### grunt.png

```text
Use case: stylized-concept. Asset type: small character sprite for an independent HTML card-battle simulator. Style: original dark fantasy, hand-painted flat-shaded cartoon, bold charcoal silhouette and clear large shapes, deliberately simple detail readable at 192 pixels, muted ink blue / dusty ochre / warm bone palette. Composition: one single full-body character, ground-level three-quarter side view, all limbs and accessories within frame, centered with only a slim transparent margin. Backdrop: genuinely transparent; no floor, shadow blob, scenery, border, frame, lettering or watermark. Keep image roughly square. Not a copy of any existing game character. Subject: a small scruffy goblin rogue, thin wiry limbs, hooded patched ochre cape and navy trousers, gripping one crude short dagger. Impish but menacing expression, slightly hunched ready stance. Faces LEFT toward a player at the center of a horizontal battle scene. Compact clean silhouette.
```

### summoner.png

```text
Use case: stylized-concept. Asset type: small character sprite for an independent HTML card-battle simulator. Style: original dark fantasy, hand-painted flat-shaded cartoon, bold charcoal silhouette and clear large shapes, deliberately simple detail readable at 192 pixels, muted ink blue / dusty ochre / warm bone palette. Composition: one single full-body character, ground-level three-quarter side view, all limbs and accessories within frame, centered with only a slim transparent margin. Backdrop: genuinely transparent; no floor, shadow blob, scenery, border, frame, lettering or watermark. Keep image roughly square. Not a copy of any existing game character. Subject: a tall armored occult summoner, heavy faceless charcoal-blue plate armor, ochre fabric tabard, one hand gripping a simple staff topped with a small floating cyan crystal; other hand raised to cast. No surrounding minions. Broad shoulder silhouette, strong boots. Faces LEFT toward a player at the center of a horizontal battle scene. Very restrained cyan accents.
```

### gluttony.png

```text
Use case: stylized-concept. Asset type: small character sprite for an independent HTML card-battle simulator. Style: original dark fantasy, hand-painted flat-shaded cartoon, bold charcoal silhouette and clear large shapes, deliberately simple detail readable at 192 pixels, muted ink blue / dusty ochre / warm bone palette. Composition: one single full-body character, ground-level three-quarter side view, all limbs and accessories within frame, centered with only a slim transparent margin. Backdrop: genuinely transparent; no floor, shadow blob, scenery, border, frame, lettering or watermark. Keep image roughly square. Not a copy of any existing game character. Subject: a squat fat glutton monster with huge round belly, stubby legs and thick arms, slate blue-gray skin, ragged ochre waistcloth, broad hungry toothy grin and two tiny horns. Carrying one small overflowing dark sack of blank scrap cards. Humorous sinister personality, no gore. Faces LEFT toward a player at the center of a horizontal battle scene. One character only.
```
