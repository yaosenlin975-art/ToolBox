import pathlib

files = [
    pathlib.Path(r'D:\Workspaces\ToolBox\Project\Themes\Lang zh-CN.xaml'),
]
for p in files:
    text = p.read_text(encoding='utf-8')
    text = text.replace('<sys:String x:Key="Lang_RecordCount">', '    <sys:String x:Key="Lang_RecordCount">')
    text = text.replace('<sys:String x:Key="Lang_SaveAs">另存为', '    <sys:String x:Key="Lang_SaveAs">另存为')
    text = text.replace('<sys:String x:Key="Lang_Copy">复制', '    <sys:String x:Key="Lang_Copy">复制')
    p.write_text(text, encoding='utf-8')
    print('fixed', p)
