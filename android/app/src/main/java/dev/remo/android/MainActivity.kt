// Remo for Android
// Copyright (C) 2026 onboard-dev
// SPDX-License-Identifier: GPL-3.0-or-later
package dev.remo.android

import android.Manifest
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import android.view.KeyEvent

object T {
    val Bg = Color(0xFF1A1B26); val Side = Color(0xFF16161E); val Card = Color(0xFF1F2335); val CardHover = Color(0xFF292E42)
    val Border = Color(0xFF2F334D); val Accent = Color(0xFF7AA2F7); val Text = Color(0xFFC0CAF5); val Sub = Color(0xFF7982A9)
    val Green = Color(0xFF9ECE6A); val Red = Color(0xFFF7768E); val Field = Color(0xFF24283B); val Yellow = Color(0xFFE0AF68)
}

class MainActivity : ComponentActivity() {
    private val vm: RemoViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge(statusBarStyle = androidx.activity.SystemBarStyle.dark(0xFF16161E.toInt()), navigationBarStyle = androidx.activity.SystemBarStyle.dark(0xFF16161E.toInt()))
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        setContent {
            MaterialTheme(
                colorScheme = darkColorScheme(
                    primary = T.Accent, onPrimary = T.Bg, background = T.Bg, surface = T.Card, onSurface = T.Text,
                    onBackground = T.Text, secondaryContainer = T.CardHover, onSecondaryContainer = T.Text,
                    surfaceVariant = T.Field, onSurfaceVariant = T.Sub, outline = T.Border, error = T.Red
                )
            ) { RemoApp(vm) }
        }
    }

    override fun onPause() { super.onPause(); vm.persist() }
}

// ======================= ルート =======================

@Composable
fun RemoApp(vm: RemoViewModel) {
    val snack = remember { SnackbarHostState() }
    LaunchedEffect(vm.message) { vm.message?.let { snack.showSnackbar(it); vm.message = null } }

    var editing by remember { mutableStateOf<Pair<Site?, Site>?>(null) }
    var deleting by remember { mutableStateOf<Site?>(null) }
    var showAbout by remember { mutableStateOf(false) }
    var exportAsk by remember { mutableStateOf(false) }
    var exportPass by remember { mutableStateOf("") }
    var importUri by remember { mutableStateOf<Uri?>(null) }

    val exportLauncher = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        if (uri != null) vm.export(uri, exportPass)
        exportPass = ""
    }
    val importLauncher = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri -> if (uri != null) importUri = uri }

    val actions = remember(vm) {
        SideActions(
            onAdd = { editing = null to Site() },
            onEdit = { editing = it to it.copy() },
            onDuplicate = { vm.duplicate(it) },
            onDelete = { deleting = it },
            onExport = { exportAsk = true },
            onImport = { importLauncher.launch(arrayOf("*/*")) },
            onAbout = { showAbout = true }
        )
    }

    Box(Modifier.fillMaxSize().background(T.Bg)) {
        BoxWithConstraints(Modifier.fillMaxSize().safeDrawingPadding()) {
            val wide = maxWidth >= 720.dp && maxWidth > maxHeight
            if (wide) {
                Row(Modifier.fillMaxSize()) {
                    Sidebar(vm, actions, Modifier.width(if (vm.sideCollapsed) 68.dp else 320.dp).fillMaxHeight(), vm.sideCollapsed, true)
                    Column(Modifier.weight(1f).fillMaxHeight()) {
                        if (vm.sessions.size >= 2) TabBar(vm)
                        TerminalPane(vm, Modifier.weight(1f).fillMaxWidth(), showBack = false)
                    }
                }
            } else {
                if (vm.showTerminal && vm.active != null) {
                    BackHandler { vm.showTerminal = false }
                    Column(Modifier.fillMaxSize()) {
                        PhoneTopBar(vm)
                        if (vm.sessions.size >= 2) TabBar(vm)
                        TerminalPane(vm, Modifier.weight(1f).fillMaxWidth(), showBack = true)
                    }
                } else {
                    Sidebar(vm, actions, Modifier.fillMaxSize(), false, false)
                }
            }
        }
        SnackbarHost(snack, Modifier.align(Alignment.BottomCenter).safeDrawingPadding().padding(bottom = 64.dp))
    }

    editing?.let { (old, copy) ->
        SiteDialog(copy, isNew = old == null, onDismiss = { editing = null }) { vm.saveSite(old, it); editing = null }
    }
    deleting?.let { s ->
        AlertDialog(
            onDismissRequest = { deleting = null },
            title = { Text("接続先の削除") },
            text = { Text("「${s.name}」を削除しますか?") },
            confirmButton = { TextButton({ vm.delete(s); deleting = null }) { Text("削除", color = T.Red) } },
            dismissButton = { TextButton({ deleting = null }) { Text("キャンセル") } }
        )
    }
    if (showAbout) AlertDialog(
        onDismissRequest = { showAbout = false },
        title = { Text("Remo for Android") },
        text = { Text("Windows 版 Remo の Android 移植版です。\nバージョン 0.1.0\n\nCopyright (C) 2026 onboard-dev\nGPL-3.0-or-later\n\nSSH 通信には JSch (mwiede 版 / BSD) を使用しています。") },
        confirmButton = { TextButton({ showAbout = false }) { Text("閉じる") } }
    )
    if (exportAsk) PasswordDialog("書き出し", "ファイルを保護するパスワードを決めてください。\n(忘れると開けません)", confirmLabel = "次へ", onDismiss = { exportAsk = false }) {
        exportPass = it; exportAsk = false; exportLauncher.launch("remo-sites.remo")
    }
    importUri?.let { u ->
        PasswordDialog("読み込み", "書き出し時に決めたパスワードを入力してください。", confirmLabel = "読み込む", onDismiss = { importUri = null }) {
            vm.import(u, it); importUri = null
        }
    }
    vm.ask?.let { a ->
        if (a.isText) PasswordDialog(a.title, a.message, "OK", secret = a.secret, onDismiss = { vm.answer(a, null) }) { vm.answer(a, it) }
        else AlertDialog(
            onDismissRequest = { vm.answer(a, "no") },
            title = { Text(a.title) }, text = { Text(a.message) },
            confirmButton = { TextButton({ vm.answer(a, "yes") }) { Text("接続する") } },
            dismissButton = { TextButton({ vm.answer(a, "no") }) { Text("やめる") } }
        )
    }
}

class SideActions(
    val onAdd: () -> Unit, val onEdit: (Site) -> Unit, val onDuplicate: (Site) -> Unit, val onDelete: (Site) -> Unit,
    val onExport: () -> Unit, val onImport: () -> Unit, val onAbout: () -> Unit
)

// ======================= 接続先リスト (サイドバー / 縦画面の一覧) =======================

@Composable
fun Sidebar(vm: RemoViewModel, a: SideActions, modifier: Modifier, collapsed: Boolean, canCollapse: Boolean) {
    var menu by remember { mutableStateOf(false) }
    Column(modifier.background(T.Side)) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            if (!collapsed) {
                Text("🔌 Remo", color = T.Text, fontSize = 20.sp, modifier = Modifier.weight(1f))
                TextButton({ a.onAdd() }) { Text("＋ 追加", color = T.Accent) }
            }
            Box {
                TextButton({ menu = true }, contentPadding = PaddingValues(8.dp)) { Text("⋮", color = T.Text, fontSize = 20.sp) }
                DropdownMenu(menu, { menu = false }) {
                    if (collapsed) DropdownMenuItem({ Text("接続先を追加") }, { menu = false; a.onAdd() })
                    DropdownMenuItem({ Text("書き出し") }, { menu = false; a.onExport() })
                    DropdownMenuItem({ Text("読み込み") }, { menu = false; a.onImport() })
                    DropdownMenuItem({ Text("文字サイズ +") }, { menu = false; vm.fontSp = (vm.fontSp + 1).coerceAtMost(36f); vm.persist() })
                    DropdownMenuItem({ Text("文字サイズ −") }, { menu = false; vm.fontSp = (vm.fontSp - 1).coerceAtLeast(7f); vm.persist() })
                    if (canCollapse) DropdownMenuItem({ Text(if (collapsed) "サイドバーを広げる" else "サイドバーを畳む") }, { menu = false; vm.sideCollapsed = !vm.sideCollapsed; vm.persist() })
                    DropdownMenuItem({ Text("Remo について") }, { menu = false; a.onAbout() })
                }
            }
        }
        if (vm.sites.isEmpty() && !collapsed) {
            Box(Modifier.fillMaxWidth().padding(24.dp), contentAlignment = Alignment.Center) {
                Text("「＋ 追加」から接続先を登録してください", color = T.Sub, fontSize = 14.sp)
            }
        }
        LazyColumn(Modifier.weight(1f), contentPadding = PaddingValues(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(vm.sites.toList(), key = { System.identityHashCode(it) }) { s -> SiteCard(vm, s, a, collapsed) }
        }
    }
}


@OptIn(ExperimentalFoundationApi::class)
@Composable
fun SiteCard(vm: RemoViewModel, s: Site, a: SideActions, collapsed: Boolean) {
    val ses = vm.sessionFor(s)
    val alive = ses?.alive == true
    val isActive = vm.active != null && vm.active === ses
    var menu by remember { mutableStateOf(false) }
    val border = when { alive -> T.Green; isActive -> T.Accent; else -> T.Border }
    Box {
        Row(
            Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(if (isActive) T.CardHover else T.Card)
                .border(BorderStroke(if (alive) 2.dp else 1.dp, border), RoundedCornerShape(12.dp))
                .combinedClickable(onClick = { vm.connect(s) }, onLongClick = { menu = true })
                .padding(if (collapsed) 8.dp else 12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(s.icon, fontSize = 26.sp, modifier = Modifier.padding(end = if (collapsed) 0.dp else 12.dp))
            if (!collapsed) {
                Column(Modifier.weight(1f)) {
                    Text(s.name.ifBlank { s.host }, color = T.Text, fontSize = 16.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text("${s.user}@${s.host}", color = T.Sub, fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    if (ses != null && ses.state == ConnState.Connecting) Text("接続中...", color = T.Yellow, fontSize = 12.sp)
                }
                if (alive) TextButton({ vm.disconnect(ses!!) }, contentPadding = PaddingValues(horizontal = 8.dp)) { Text("切断", color = T.Red) }
                else TextButton({ vm.connect(s) }, contentPadding = PaddingValues(horizontal = 8.dp)) { Text("接続", color = T.Green) }
                TextButton({ a.onEdit(s) }, contentPadding = PaddingValues(horizontal = 8.dp)) { Text("編集", color = T.Accent) }
            }
        }
        DropdownMenu(menu, { menu = false }) {
            DropdownMenuItem({ Text("編集") }, { menu = false; a.onEdit(s) })
            DropdownMenuItem({ Text("複製") }, { menu = false; a.onDuplicate(s) })
            if (alive) DropdownMenuItem({ Text("切断") }, { menu = false; vm.disconnect(ses!!) })
            DropdownMenuItem({ Text("削除", color = T.Red) }, { menu = false; a.onDelete(s) })
        }
    }
}

// ======================= タブ・上部バー =======================

@Composable
fun TabBar(vm: RemoViewModel) {
    Row(Modifier.fillMaxWidth().background(T.Side).horizontalScroll(rememberScrollState()).padding(horizontal = 6.dp, vertical = 4.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        for (s in vm.sessions.toList()) {
            val sel = s === vm.active
            Row(
                Modifier.clip(RoundedCornerShape(8.dp)).background(if (sel) T.Card else T.Side)
                    .border(1.dp, if (sel) T.Accent else T.Border, RoundedCornerShape(8.dp))
                    .clickable { vm.active = s }.padding(start = 10.dp, top = 2.dp, bottom = 2.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                val dot = when (s.state) { ConnState.Connected -> T.Green; ConnState.Connecting -> T.Yellow; else -> T.Sub }
                Box(Modifier.size(8.dp).clip(RoundedCornerShape(4.dp)).background(dot))
                Text(" ${s.site.icon} ${s.displayName}", color = if (sel) T.Text else T.Sub, fontSize = 13.sp, maxLines = 1,
                    overflow = TextOverflow.Ellipsis, modifier = Modifier.widthIn(max = 160.dp))
                TextButton({ vm.closeSession(s) }, contentPadding = PaddingValues(horizontal = 8.dp), modifier = Modifier.heightIn(min = 32.dp).widthIn(min = 32.dp)) {
                    Text("✕", color = T.Sub, fontSize = 13.sp)
                }
            }
        }
    }
}

@Composable
fun PhoneTopBar(vm: RemoViewModel) {
    val s = vm.active ?: return
    var menu by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth().background(T.Side).padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        TextButton({ vm.showTerminal = false }) { Text("← 接続先", color = T.Accent) }
        Text("${s.site.icon} ${s.displayName}", color = T.Text, fontSize = 15.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        Box {
            TextButton({ menu = true }, contentPadding = PaddingValues(8.dp)) { Text("⋮", color = T.Text, fontSize = 20.sp) }
            DropdownMenu(menu, { menu = false }) {
                if (s.alive) DropdownMenuItem({ Text("切断") }, { menu = false; vm.disconnect(s) })
                else DropdownMenuItem({ Text("再接続") }, { menu = false; vm.connect(s.site) })
                DropdownMenuItem({ Text("このタブを閉じる") }, { menu = false; vm.closeSession(s) })
                DropdownMenuItem({ Text("文字サイズ +") }, { menu = false; vm.fontSp = (vm.fontSp + 1).coerceAtMost(36f); vm.persist() })
                DropdownMenuItem({ Text("文字サイズ −") }, { menu = false; vm.fontSp = (vm.fontSp - 1).coerceAtLeast(7f); vm.persist() })
            }
        }
    }
}

// ======================= ターミナル =======================

@Composable
fun TerminalPane(vm: RemoViewModel, modifier: Modifier, showBack: Boolean) {
    val s = vm.active
    var view by remember { mutableStateOf<TerminalView?>(null) }
    var hasSel by remember { mutableStateOf(false) }
    var ctrl by remember { mutableStateOf(false) }
    var alt by remember { mutableStateOf(false) }

    Column(modifier.background(T.Bg)) {
        Box(Modifier.weight(1f).fillMaxWidth()) {
            AndroidView(
                factory = { ctx ->
                    TerminalView(ctx).also { v ->
                        v.fontSp = vm.fontSp
                        v.onNotify = { vm.message = it }
                        v.onSelectionChanged = { hasSel = it }
                        v.onFontChanged = { vm.fontSp = it; vm.persist() }
                        v.onModifiersUsed = { ctrl = v.ctrlOn; alt = v.altOn }
                        view = v
                    }
                },
                update = { v ->
                    if (v.session !== s) { v.bind(s); if (s != null) v.post { v.showKeyboard() } }
                    if (v.fontSp != vm.fontSp) v.fontSp = vm.fontSp
                },
                modifier = Modifier.fillMaxSize()
            )
            if (s == null) {
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Text("接続先をタップして接続します", color = T.Sub)
                }
            }
            if (hasSel) {
                Row(Modifier.align(Alignment.TopEnd).padding(8.dp).clip(RoundedCornerShape(12.dp)).background(T.Card).border(1.dp, T.Accent, RoundedCornerShape(12.dp))) {
                    TextButton({ view?.copySelection(); hasSel = false }) { Text("コピー", color = T.Accent) }
                    TextButton({ view?.copyAll(); hasSel = false }) { Text("すべてコピー", color = T.Accent) }
                    TextButton({ view?.clearSelection(); hasSel = false }) { Text("解除", color = T.Sub) }
                }
            }
            if (s != null && s.state == ConnState.Closed) {
                Row(Modifier.align(Alignment.BottomEnd).padding(8.dp).clip(RoundedCornerShape(12.dp)).background(T.Card).border(1.dp, T.Border, RoundedCornerShape(12.dp))) {
                    TextButton({ vm.connect(s.site) }) { Text("再接続", color = T.Green) }
                }
            }
        }
        if (s != null) ExtraKeys(view, ctrl, alt, { ctrl = it; view?.ctrlOn = it }, { alt = it; view?.altOn = it })
    }
}

@Composable
fun KeyBtn(label: String, active: Boolean = false, onTap: () -> Unit) {
    // pointerInput を使い、IME の表示を維持するためフォーカスを奪わない
    Box(
        Modifier.padding(horizontal = 2.dp, vertical = 4.dp).heightIn(min = 40.dp).widthIn(min = 44.dp).clip(RoundedCornerShape(8.dp))
            .background(if (active) T.Accent else T.Card)
            .pointerInput(Unit) { detectTapGestures { onTap() } }.padding(horizontal = 10.dp),
        contentAlignment = Alignment.Center
    ) { Text(label, color = if (active) T.Bg else T.Text, fontSize = 14.sp, fontFamily = FontFamily.Monospace) }
}

@Composable
fun ExtraKeys(view: TerminalView?, ctrl: Boolean, alt: Boolean, setCtrl: (Boolean) -> Unit, setAlt: (Boolean) -> Unit) {
    fun key(code: Int) = { view?.sendKeyCode(code); Unit }
    Row(Modifier.fillMaxWidth().background(T.Side).horizontalScroll(rememberScrollState()).padding(horizontal = 4.dp)) {
        KeyBtn("Esc", onTap = key(KeyEvent.KEYCODE_ESCAPE))
        KeyBtn("Tab", onTap = key(KeyEvent.KEYCODE_TAB))
        KeyBtn("Ctrl", ctrl) { setCtrl(!ctrl) }
        KeyBtn("Alt", alt) { setAlt(!alt) }
        KeyBtn("←", onTap = key(KeyEvent.KEYCODE_DPAD_LEFT))
        KeyBtn("↓", onTap = key(KeyEvent.KEYCODE_DPAD_DOWN))
        KeyBtn("↑", onTap = key(KeyEvent.KEYCODE_DPAD_UP))
        KeyBtn("→", onTap = key(KeyEvent.KEYCODE_DPAD_RIGHT))
        KeyBtn("Home", onTap = key(KeyEvent.KEYCODE_MOVE_HOME))
        KeyBtn("End", onTap = key(KeyEvent.KEYCODE_MOVE_END))
        KeyBtn("PgUp", onTap = key(KeyEvent.KEYCODE_PAGE_UP))
        KeyBtn("PgDn", onTap = key(KeyEvent.KEYCODE_PAGE_DOWN))
        for (c in listOf("|", "/", "-", "~", "_", "\\", "$", "&", ":", "\"")) KeyBtn(c) { view?.sendRaw(c) }
        KeyBtn("貼付") { view?.paste() }
        KeyBtn("全消去") { view?.clearScreen() }
        KeyBtn("⌨") { view?.showKeyboard() }
    }
}

// ======================= ダイアログ =======================

@Composable
fun PasswordDialog(title: String, message: String, confirmLabel: String, secret: Boolean = true, onDismiss: () -> Unit, onOk: (String) -> Unit) {
    var v by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column {
                Text(message, fontSize = 14.sp)
                Spacer(Modifier.height(8.dp))
                OutlinedTextField(
                    v, { v = it }, singleLine = true,
                    visualTransformation = if (secret) PasswordVisualTransformation() else VisualTransformation.None,
                    keyboardOptions = KeyboardOptions(keyboardType = if (secret) KeyboardType.Password else KeyboardType.Text)
                )
            }
        },
        confirmButton = { TextButton({ onOk(v) }) { Text(confirmLabel) } },
        dismissButton = { TextButton(onDismiss) { Text("キャンセル") } }
    )
}

private val ICONS = listOf("🖥️", "💻", "🐧", "🍓", "☁️", "🏫", "🔧", "🧪", "🚀", "🗄️", "🌐", "🏠", "🔒", "📡", "🛠️", "🎮")

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun SiteDialog(s: Site, isNew: Boolean, onDismiss: () -> Unit, onSave: (Site) -> Unit) {
    var name by remember { mutableStateOf(s.name) }
    var host by remember { mutableStateOf(s.host) }
    var user by remember { mutableStateOf(s.user) }
    var pass by remember { mutableStateOf(s.pass) }
    var showPass by remember { mutableStateOf(false) }
    var enc by remember { mutableIntStateOf(s.enc) }
    var lang by remember { mutableStateOf(s.lang) }
    var icon by remember { mutableStateOf(s.icon.ifEmpty { ICONS[0] }) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(if (isNew) "接続先を追加" else "接続先を編集") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                OutlinedTextField(name, { name = it }, label = { Text("サイト名") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(host, { host = it }, label = { Text("接続先 (例: 192.0.2.10:2222)") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri))
                OutlinedTextField(user, { user = it }, label = { Text("ユーザー名") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(pass, { pass = it }, label = { Text("パスワード") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                    visualTransformation = if (showPass) VisualTransformation.None else PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    trailingIcon = { TextButton({ showPass = !showPass }) { Text(if (showPass) "隠す" else "表示", fontSize = 12.sp) } })
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("文字コード", Modifier.padding(end = 8.dp), fontSize = 14.sp)
                    FilterChip(enc == 0, { enc = 0 }, { Text("UTF-8") })
                    Spacer(Modifier.width(8.dp))
                    FilterChip(enc == 1, { enc = 1 }, { Text("Shift_JIS") })
                }
                OutlinedTextField(lang, { lang = it }, label = { Text("LANG (空なら送らない)") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                Text("アイコン", fontSize = 14.sp)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    for (i in ICONS) Box(
                        Modifier.size(44.dp).clip(RoundedCornerShape(10.dp)).background(if (i == icon) T.CardHover else T.Field)
                            .border(if (i == icon) 2.dp else 1.dp, if (i == icon) T.Accent else T.Border, RoundedCornerShape(10.dp))
                            .clickable { icon = i }, contentAlignment = Alignment.Center
                    ) { Text(i, fontSize = 22.sp) }
                }
            }
        },
        confirmButton = {
            TextButton(enabled = host.isNotBlank(), onClick = {
                val r = s.copy()
                r.name = name.trim().ifEmpty { host.trim() }; r.host = host.trim(); r.user = user.trim(); r.pass = pass
                r.enc = enc; r.lang = lang.trim(); r.icon = icon
                onSave(r)
            }) { Text("保存") }
        },
        dismissButton = { TextButton(onDismiss) { Text("キャンセル") } }
    )
}

