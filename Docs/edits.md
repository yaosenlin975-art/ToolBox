# 说明
只修改完成状态, 禁止修改内容

# Mini窗口
## 待办
### 修改
### Bug
- [x] 新增待办时点击确定后软件会卡死（已修复：QuickAddTodo_Click 改用 AddAsync，消除 sync-over-async 死锁）
- [x] 未显示任何代办, 新增代办也不会刷新（已修复：死锁解除后 ItemsChanged 可正常触发 UI 刷新）


## 助手
### 新增
### 修改
### Bug

## 历史
### 新增
- [x] 对已有的截图右键要显示菜单: 复制 另存为（已修复：HistoryView 缩略图添加 MouseRightButtonDown 事件）
- [x] 点击已有的截图弹出的大图要允许右键菜单（已修复：ImagePreviewWindow 添加右键菜单，复制/另存为）
### 修改
### Bug

## 其他
### 新增
### 修改
### Bug

# 完整窗口
## 设置
### 新增
- [x] 截图历史最大时效, 超过时效后自动清理 (已实现 - ScreenshotMaxAge 设置 + CacheManager.CleanupExpired)
### 修改
- [x] 关闭提示弹窗风格要与主窗口风格一致 (已实现 - CloseDialogWindow 自定义弹窗，深色主题风格)
### Bug
- [x] 浮动截图的透明度拖动条设置的透明度没有生效（已修复：ScrapBook.AddScrap 调用 ApplyScrapOption 应用 ScrapOption InactiveAlpha/MouseOverAlpha）

## 助手
### 新增
- [x] 左侧会话管理栏中鼠标悬浮在会话item上时在会话item内部最右侧显示删除按钮 (已实现 - DeleteBtn hover 可见)
### Bug

## 其他
### 抽屉图标
#### Bug
- [x] 鼠标右键菜单所有按钮都没有反应 (已修复 - Placement=Absolute + HorizontalOffset/VerticalOffset 坐标定位)

### 浮动截图
#### 新增
#### 修改
#### Bug
- [x] 小图已完成裁切, 但没有改变实际大小(浮窗大小应该改为50*50) (已修复 - maxSize: 100 → 50)
- [x] 右键菜单位置偏移过大, 完全脱离浮动截图（已修复：ScrapWindow.ShowContextMenu 改用 PlacementMode.Mouse）
