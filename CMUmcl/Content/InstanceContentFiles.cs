// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace CMUmcl.Content;

internal sealed class InstanceContentFile
{
    internal string Path { get; set; } = "";
    internal string Name { get; set; } = "";
    internal string Description { get; set; } = "";
    internal BitmapSource? Icon {get;set;}
    internal bool Disabled => Path.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)).Equals(".cmumcl-disabled-resourcepacks",StringComparison.OrdinalIgnoreCase);
}

// All mutations stay below a resolved game directory. Reparse points are never followed.
internal sealed class InstanceContentFiles
{
    internal string Root { get; }
    internal InstanceContentFiles(string root) { Root=System.IO.Path.GetFullPath(root); }
    internal string Folder(string relative)
    {
        string path=System.IO.Path.GetFullPath(System.IO.Path.Combine(Root,relative));
        if(!path.Equals(Root,StringComparison.OrdinalIgnoreCase) && !path.StartsWith(Root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("目录越界。");
        for(string? part=path;part!=null;part=System.IO.Path.GetDirectoryName(part))
        {
            if((Directory.Exists(part)||File.Exists(part)) && (File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("暂不管理链接目录或链接文件。");
        }
        return path;
    }
    internal IReadOnlyList<InstanceContentFile> List(string relative,bool mods)
    {
        string directory=Folder(relative);
        var paths=Directory.Exists(directory)?Directory.GetFiles(directory).Where(p=>IsSupported(p,mods)).ToList():new List<string>();
        if(!mods)
        {
            if(Directory.Exists(directory))paths.AddRange(Directory.GetDirectories(directory).Where(p=>File.Exists(System.IO.Path.Combine(p,"pack.mcmeta"))));
            string disabled=Folder(".cmumcl-disabled-resourcepacks");if(Directory.Exists(disabled))paths.AddRange(Directory.GetDirectories(disabled));
        }
        return paths.OrderBy(System.IO.Path.GetFileName,StringComparer.OrdinalIgnoreCase).Select(p=>Read(Folder(p),mods)).ToArray();
    }
    internal static bool IsSupported(string path,bool mods)
    {
        string name=path.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase)?path.Substring(0,path.Length-9):path;
        return name.EndsWith(mods?".jar":".zip",StringComparison.OrdinalIgnoreCase);
    }
    private static InstanceContentFile Read(string path,bool mods)
    {
        var item=new InstanceContentFile{Path=path,Name=System.IO.Path.GetFileName(path)};
        if(!mods)item.Icon=ReadPackIcon(path);
        try
        {
            if(Directory.Exists(path)){string metadata=System.IO.Path.Combine(path,"pack.mcmeta");if((File.GetAttributes(metadata)&FileAttributes.ReparsePoint)==0&&new FileInfo(metadata).Length<=256*1024){using var json=JsonDocument.Parse(File.ReadAllText(metadata));var description=json.RootElement.GetProperty("pack").GetProperty("description");if(description.ValueKind==JsonValueKind.String)item.Description=description.GetString()??"";}return item;}
            using var zip=ZipFile.OpenRead(path);
            var entry=zip.GetEntry(mods?"fabric.mod.json":"pack.mcmeta");
            if(entry!=null && entry.Length<=256*1024)
            {
                using var stream=entry.Open();using var json=JsonDocument.Parse(stream);
                var data=json.RootElement;
                if(mods)
                {
                    if(data.TryGetProperty("name",out var name)&&name.ValueKind==JsonValueKind.String)item.Name=name.GetString()??item.Name;
                    if(data.TryGetProperty("description",out var description)&&description.ValueKind==JsonValueKind.String)item.Description=description.GetString()??"";
                }
                else if(data.TryGetProperty("pack",out var pack)&&pack.TryGetProperty("description",out var description)&&description.ValueKind==JsonValueKind.String)item.Description=description.GetString()??"";
            }
        }
        catch { /* Broken/unknown metadata must not hide an otherwise manageable file. */ }
        return item;
    }
    /// <summary>Fabric/Quilt mod id and version from fabric.mod.json, or null for other files.</summary>
    internal static (string Id,string Version)? ReadFabricModId(string jar)
    {
        try
        {
            using var zip=ZipFile.OpenRead(jar);
            var entry=zip.GetEntry("fabric.mod.json");if(entry==null||entry.Length>256*1024)return null;
            using var stream=entry.Open();using var json=JsonDocument.Parse(stream);var data=json.RootElement;
            string id=data.TryGetProperty("id",out var i)&&i.ValueKind==JsonValueKind.String?i.GetString()??"":"";
            string version=data.TryGetProperty("version",out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
            return id.Length==0?null:(id,version);
        }
        catch{return null;}
    }
    internal static BitmapSource? ReadPackIcon(string path)
    {
        const int limit=2*1024*1024;
        try
        {
            if(Directory.Exists(path))
            {
                string cover=System.IO.Path.Combine(path,"pack.png");
                if(!File.Exists(cover)||(File.GetAttributes(cover)&FileAttributes.ReparsePoint)!=0||new FileInfo(cover).Length>limit)return null;
                using var input=File.OpenRead(cover);return Read(input);
            }
            using var zip=ZipFile.OpenRead(path);var entry=zip.GetEntry("pack.png");if(entry==null||entry.Length>limit)return null;
            using var stream=entry.Open();return Read(stream);
        }
        catch{return null;}
        BitmapSource? Read(Stream input)
        {
            using var copy=new MemoryStream();byte[] buffer=new byte[8192];int count;
            while((count=input.Read(buffer,0,buffer.Length))>0){if(copy.Length+count>limit)return null;copy.Write(buffer,0,count);}
            copy.Position=0;return CMUmcl.Community.ResourceIconLoader.Decode(copy);
        }
    }
    internal void Toggle(string path)
    {
        path=CheckedFile(path);string target;
        if(Directory.Exists(path))
        {
            bool disabled=System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)).Equals(".cmumcl-disabled-resourcepacks",StringComparison.OrdinalIgnoreCase);
            string parent=Folder(disabled?"resourcepacks":".cmumcl-disabled-resourcepacks");Directory.CreateDirectory(parent);target=System.IO.Path.Combine(parent,System.IO.Path.GetFileName(path));
        }
        else target=path.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase)?path.Substring(0,path.Length-9):path+".disabled";
        if(File.Exists(target)||Directory.Exists(target))throw new IOException("目标文件已存在，未覆盖原文件。");
        if(Directory.Exists(path))Directory.Move(path,target);else File.Move(path,target);
    }
    internal string Remove(string path)
    {
        path=CheckedFile(path);string trash=Folder(System.IO.Path.Combine(".cmumcl-trash",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(trash);string target=System.IO.Path.Combine(trash,System.IO.Path.GetFileName(path));if(Directory.Exists(path))Directory.Move(path,target);else File.Move(path,target);return target;
    }
    internal void Install(string relative,string source,bool mods)
    {
        if(!IsSupported(source,mods)||source.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase))throw new IOException("文件类型不支持。");
        string directory=Folder(relative);Directory.CreateDirectory(directory);
        string destination=Folder(System.IO.Path.Combine(relative,System.IO.Path.GetFileName(source)));
        if(File.Exists(destination)||File.Exists(destination+".disabled"))throw new IOException("同名文件已存在，未覆盖。");
        string temp=System.IO.Path.Combine(directory,".cmumcl-install-"+Guid.NewGuid().ToString("N"));
        try { File.Copy(source,temp,false);File.Move(temp,destination); } finally { if(File.Exists(temp))File.Delete(temp); }
    }
    private string CheckedFile(string path)
    {
        string full=System.IO.Path.GetFullPath(path);string prefix=Root.TrimEnd('\\')+"\\";
        if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("文件越界。");
        full=Folder(full.Substring(prefix.Length));
        string relative=full.Substring(prefix.Length);
        if(!(relative.StartsWith("mods\\",StringComparison.OrdinalIgnoreCase)||relative.StartsWith("resourcepacks\\",StringComparison.OrdinalIgnoreCase)||relative.StartsWith(".cmumcl-disabled-resourcepacks\\",StringComparison.OrdinalIgnoreCase)) || relative.Substring(relative.IndexOf('\\')+1).Contains('\\'))throw new IOException("文件不在管理目录中。");
        if(!File.Exists(full)&&!Directory.Exists(full))throw new IOException("文件已不存在，请刷新列表。");return full;
    }
}
