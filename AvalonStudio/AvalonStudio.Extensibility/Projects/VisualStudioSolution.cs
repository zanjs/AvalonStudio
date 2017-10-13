﻿using AvalonStudio.Projects;
using AvalonStudio.Shell;
using AvalonStudio.Utils;
using Microsoft.DotNet.Cli.Sln.Internal;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace AvalonStudio.Extensibility.Projects
{
    public class VisualStudioSolution : ISolution
    {
        private SlnFile _solutionModel;
        private Dictionary<Guid, ISolutionItem> _solutionItems;

        public const string Extension = "sln";

        public static VisualStudioSolution Load(string fileName)
        {
            return new VisualStudioSolution(SlnFile.Read(fileName));
        }

        public static VisualStudioSolution Create(string location, string name, bool save = true)
        {
            var filePath = Path.Combine(location, name + "." + Extension);

            var result = new VisualStudioSolution(new SlnFile { FullPath = filePath, FormatVersion = "12.00", MinimumVisualStudioVersion = "10.0.40219.1", VisualStudioVersion = "15.0.27009.1" });

            if(save)
            {
                result.Save();
            }

            return result;
        }            

        private VisualStudioSolution(SlnFile solutionModel)
        {
            var shell = IoC.Get<IShell>();
            _solutionModel = solutionModel;
            _solutionItems = new Dictionary<Guid, ISolutionItem>();

            Parent = Solution = this;

            Id = Guid.NewGuid();

            Items = new ObservableCollection<ISolutionItem>();

            LoadFolders();

            LoadProjects();

            ResolveReferences();

            BuildTree();

            this.PrintTree();
        }

        private void BuildTree()
        {
            var nestedProjects = _solutionModel.Sections.FirstOrDefault(section => section.Id == "NestedProjects");

            if (nestedProjects != null)
            {
                foreach (var nestedProject in nestedProjects.Properties)
                {
                    _solutionItems[Guid.Parse(nestedProject.Key)].SetParentInternal(_solutionItems[Guid.Parse(nestedProject.Value)] as ISolutionFolder);
                }
            }
        }

        private void LoadFolders()
        {
            var solutionFolders = _solutionModel.Projects.Where(p => p.TypeGuid == ProjectTypeGuids.SolutionFolderGuid);

            foreach (var solutionFolder in solutionFolders)
            {
                var newItem = new SolutionFolder(solutionFolder.Name);
                newItem.Id = Guid.Parse(solutionFolder.Id);
                newItem.Parent = this;
                newItem.Solution = this;
                _solutionItems.Add(newItem.Id, newItem);
                Items.InsertSorted(newItem, false);
            }
        }

        private void ResolveReferences()
        {
            foreach (var project in Projects)
            {
                project.ResolveReferences();
            }
        }

        private void LoadProjects()
        {
            var solutionProjects = _solutionModel.Projects.Where(p => p.TypeGuid != ProjectTypeGuids.SolutionFolderGuid);

            foreach (var project in solutionProjects)
            {
                var projectType = project.GetProjectType();

                IProject newProject = null;

                var projectLocation = Path.Combine(this.CurrentDirectory, project.FilePath);

                if (projectType != null)
                {
                    newProject = projectType.Load(this, projectLocation);
                }
                else
                {
                    newProject = new UnsupportedProjectType(this, projectLocation);
                }

                newProject.Id = Guid.Parse(project.Id);
                newProject.Solution = this;
                (newProject as ISolutionItem).Parent = this;

                _solutionItems.Add(newProject.Id, newProject);
                Items.InsertSorted(newProject);
            }
        }

        public string Name => Path.GetFileNameWithoutExtension(_solutionModel.FullPath);

        public string Location => _solutionModel.FullPath;

        public IProject StartupProject { get; set; }

        public IEnumerable<IProject> Projects => _solutionItems.Select(kv => kv.Value).OfType<IProject>();

        public string CurrentDirectory => Path.GetDirectoryName(_solutionModel.FullPath) + Platforms.Platform.DirectorySeperator;

        public ObservableCollection<ISolutionItem> Items { get; private set; }

        public ISolution Solution { get; set; }

        public ISolutionFolder Parent { get; set; }

        public Guid Id { get; set; }

        public T AddItem<T>(T item, ISolutionFolder parent = null) where T : ISolutionItem
        {
            item.Id = Guid.NewGuid();
            item.Solution = this;

            if (item is IProject project)
            {
                var currentProject = Projects.FirstOrDefault(p => p.Location == project.Location);

                if (currentProject == null)
                {
                    SetItemParent(project, parent ?? this);

                    _solutionItems.Add(project.Id, project);

                    _solutionModel.Projects.Add(new SlnProject
                    {
                        Id = project.Id.GetGuidString(),
                        TypeGuid = project.ProjectTypeId.GetGuidString(),
                        Name = project.Name,
                        FilePath = CurrentDirectory.MakeRelativePath(project.Location)
                    });

                    return (T)project;
                }
                else
                {
                    SetItemParent(currentProject, parent ?? this);

                    return (T)currentProject;
                }
            }
            else if (item is ISolutionFolder folder)
            {
                SetItemParent(folder, parent ?? this);

                _solutionModel.Projects.Add(new SlnProject
                {
                    Id = folder.Id.GetGuidString(),
                    TypeGuid = ProjectTypeGuids.SolutionFolderGuid,
                    Name = folder.Name,
                    FilePath = folder.Name
                });

                return (T)folder;
            }

            return item;
        }

        public void RemoveItem(ISolutionItem item)
        {
            SetItemParent(item, null);

            _solutionItems.Remove(item.Id);

            var currentSlnProject = _solutionModel.Projects.FirstOrDefault(slnProj => Guid.Parse(slnProj.Id) == item.Id);

            if (currentSlnProject != null)
            {
                _solutionModel.Projects.Remove(currentSlnProject);
            }
        }

        public ISourceFile FindFile(string path)
        {
            throw new NotImplementedException();
        }

        public void Save()
        {
            _solutionModel.Write();
        }

        public void SetItemParent(ISolutionItem item, ISolutionFolder parent)
        {
            var nestedProjects = _solutionModel.Sections.FirstOrDefault(section => section.Id == "NestedProjects");

            if (nestedProjects != null)
            {
                nestedProjects.Properties.Remove(item.Id.GetGuidString());
            }

            item.SetParentInternal(parent);

            if (parent != this)
            {
                if(nestedProjects == null)
                {
                    _solutionModel.Sections.Add(new SlnSection() { Id = "NestedProjects", SectionType = SlnSectionType.PreProcess });
                    nestedProjects = _solutionModel.Sections.FirstOrDefault(section => section.Id == "NestedProjects");
                }

                if (parent != null)
                {
                    nestedProjects.Properties[item.Id.GetGuidString()] = parent.Id.GetGuidString();
                }
            }
        }

        public int CompareTo(ISolutionItem other)
        {
            return this.DefaultCompareTo(other);
        }
    }
}