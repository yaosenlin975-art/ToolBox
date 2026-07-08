# 说明
只修改完成状态, 禁止修改内容

# Mini窗口
## 待办
### 修改
### Bug
- [x] 新增待办时点击确定后软件会卡死 (已修复 - TodoStore.Add 死锁：GetAwaiter().GetResult 改为 Task.Run)

## 助手
### 新增
### 修改
- [x] (未实现)输入框回车键为发送, shift+enter为换行 (已实现 - InputBox_KeyDown 处理)
### Bug

## 历史
### 修改
### Bug
- [x] 被关闭的截图浮窗不应该从历史中消失 (已修复 - ScrapBook.OnScrapClose 不再调用 ScrapRemoved)

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
