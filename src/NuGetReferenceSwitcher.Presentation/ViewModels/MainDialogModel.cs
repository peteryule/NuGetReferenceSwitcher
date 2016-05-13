﻿//-----------------------------------------------------------------------
// <copyright file="MainDialogModel.cs" company="NuGet Reference Switcher">
//     Copyright (c) Rico Suter. All rights reserved.
// </copyright>
// <license>http://nugetreferenceswitcher.codeplex.com/license</license>
// <author>Rico Suter, mail@rsuter.com</author>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using EnvDTE;
using EnvDTE80;
using MyToolkit.Build;
using MyToolkit.Collections;
using MyToolkit.Mvvm;
using MyToolkit.Utilities;
using NuGetReferenceSwitcher.Presentation.Models;
using VSLangProj;

namespace NuGetReferenceSwitcher.Presentation.ViewModels
{
    public class MainDialogModel : ViewModelBase
    {
        private Assembly _extensionAssembly;

        public MainDialogModel()
        {
            NonBWFProjects = new ExtendedObservableCollection<ProjectModel>();
            BWFProjects = new ExtendedObservableCollection<ProjectModel>();
            Transformations = new ExtendedObservableCollection<FromNuGetToProjectTransformation>();

            RemoveProjects = false;
            SaveProjects = true;
        }

        /// <summary>Gets the projects of the current solution. </summary>
        public ExtendedObservableCollection<ProjectModel> NonBWFProjects { get; private set; }

        public ExtendedObservableCollection<ProjectModel> BWFProjects { get; private set; }

        /// <summary>Gets the NuGet to project switches which are shown in the first tab. </summary>
        public ExtendedObservableCollection<FromNuGetToProjectTransformation> Transformations { get; private set; }

        /// <summary>Gets or sets a value indicating whether the changed projects should be saved. </summary>
        public bool SaveProjects { get; set; }

        /// <summary>Gets or sets a value indicating whether the previously referenced projects should be removed. </summary>
        public bool RemoveProjects { get; set; }

        /// <summary>Gets or sets the Visual Studio application object. </summary>
        public DTE Application { get; set; }

        /// <summary>Gets or sets the used UI dispatcher. </summary>
        public Dispatcher Dispatcher { get; set; }

        /// <summary>Gets or sets the assembly of the extension. </summary>
        public Assembly ExtensionAssembly
        {
            get { return _extensionAssembly; }
            set
            {
                if (Set(ref _extensionAssembly, value))
                    RaisePropertyChanged(() => ExtensionVersion);
            }
        }

        /// <summary>Gets the current extension version.</summary>
        public string ExtensionVersion
        {
            get { return ExtensionAssembly != null ? ExtensionAssembly.GetVersionWithBuildTime() : "n/a"; }
        }

        /// <summary>Initializes the view model. Must only be called once per view model instance 
        /// (after the InitializeComponent method of a <see cref="!:UserControl"/>). </summary>
        public async override void Initialize()
        {
            List<ProjectModel> projects = null;
            await RunTaskAsync(token => Task.Run(() =>
            {
                if (Application != null)
                {
                    if (Application.Solution != null)
                        projects =
                            GetAllProjects(Application.Solution.Projects.OfType<Project>(), false);
                    //GetAllProjects(
                    //    Application.Solution.Projects.OfType<Project>());
                }
            }, token));

            NonBWFProjects.Initialize(projects);
            Transformations.Initialize(projects
                .SelectMany(p => p.NuGetReferences.Where(z => 
                    z.Name.StartsWith("bwf", StringComparison.OrdinalIgnoreCase)
                    || z.Name.StartsWith("brady", StringComparison.OrdinalIgnoreCase)))
                .GroupBy(r => r.Name)
                .Select(g => new FromNuGetToProjectTransformation(projects, g.First()))
                .OrderBy(s => s.FromAssemblyName));

            //BWFProjects.AddRange(GetAllProjects(Application.Solution.Projects.OfType<Project>(), true).GroupBy(r => r.Name)
            //    .Select(g =>  g.First()).OrderBy(s => s.Name));
        }

        /// <summary>Switches the NuGet DLL references to the selected project references. </summary>
        /// <returns>The task. </returns>
        public async Task SwitchToProjectReferencesAsync()
        {
            await RunTaskAsync(token => Task.Run(() =>
            {
                // first, add all required projects
                var activeTransformations = Transformations
                    .Where(p => p.SelectedMode != NuGetToProjectMode.Deactivated)
                    .ToList();

                foreach (var transformation in activeTransformations)
                {
                    AddProjectToSolutionIfNeeded(transformation);
                }

                var allNonBwfProjects = GetAllProjects(Application.Solution.Projects.OfType<Project>(), false);
                var allBwfProjects = GetAllProjects(Application.Solution.Projects.OfType<Project>(), true);

                // then, run through all projects and replace nuget dependencies with project references
                foreach (var project in allNonBwfProjects)
                {
                    var nuGetReferenceTransformationsForProject = "";
                    foreach (var assemblyToProjectSwitch in activeTransformations)
                    {
                        // take equality not on complete path, but on assembly name
                        var reference = project.NuGetReferences
                            .FirstOrDefault(r => r.Name == assemblyToProjectSwitch.FromAssemblyName);

                        if (reference != null)
                        {
                            if (assemblyToProjectSwitch.ToProject == null)
                                assemblyToProjectSwitch.ToProject = allBwfProjects.SingleOrDefault(x => x.Name == reference.Name);

                            if (assemblyToProjectSwitch.ToProject != null)
                            {
                                var fromAssemblyPath = reference.Path;
                                reference.Remove();


                                project.AddProjectReference(assemblyToProjectSwitch.ToProject);
                                nuGetReferenceTransformationsForProject +=
                                    assemblyToProjectSwitch.ToProject.Name + "\t" +
                                    PathUtilities.MakeRelative(assemblyToProjectSwitch.ToProject.Path,
                                        project.CurrentConfigurationPath) + "\t" +
                                    PathUtilities.MakeRelative(fromAssemblyPath, project.CurrentConfigurationPath) +
                                    "\n";
                            }
                            else
                            {
                                MessageBox.Show(
                                    "Cannot switch from assembly '" + assemblyToProjectSwitch.FromAssemblyName + "' to project '" + assemblyToProjectSwitch.ToProjectPath +
                                    "' because project is not loaded. ", "Project not loaded", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        }
                    }

                    // TODO: Save project
                    //if (SaveProjects)
                    //    project.Save();

                    if (!string.IsNullOrEmpty(nuGetReferenceTransformationsForProject))
                    {
                        File.AppendAllText(project.CurrentConfigurationPath, nuGetReferenceTransformationsForProject);
                    }
                }
            }, token));
        }

        /// <summary>Handles an exception which occured in the <see cref="M:MyToolkit.Mvvm.ViewModelBase.RunTaskAsync(System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task})"/> method. </summary>
        /// <param name="exception">The exception. </param>
        public override void HandleException(Exception exception)
        {
            MessageBox.Show(exception.Message, "An error occurred", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>Switches the project references to the previously referenced NuGet DLLs. </summary>
        /// <returns>The task. </returns>
        public async Task SwitchToNuGetReferencesAsync()
        {
            await RunTaskAsync(token => Task.Run(() =>
            {
                var projectsToRemove = GetCurrentProjectsToRemove();
                foreach (var project in NonBWFProjects)
                {
                    foreach (var transformation in project.CurrentToNuGetTransformations)
                    {
                        var reference = project.References
                            .FirstOrDefault(r => r.ProjectName == transformation.FromProjectName);

                        if (reference != null)
                        {
                            reference.Remove();

                            var successfullyAdded = project.AddReference(transformation.ToAssemblyPath);
                            if (!successfullyAdded)
                            {
                                MessageBox.Show("The project '" + transformation.ToAssemblyPath + "' could not be added. " +
                                                "\nSkipped.", "Could not add project", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        }
                    }

                    // TODO: Save project
                    //if (SaveProjects)
                    //    project.Save();

                    project.DeleteConfigurationFile();
                }

                RemoveProjectsFromSolution(projectsToRemove);
            }, token));
        }

        //private List<ProjectModel> GetAllNonBwfProjects(IEnumerable<Project> objects)
        //{
        //    return GetAllProjects(filteredObjects);
        //}

        //private List<ProjectModel> GetAllBwfProjects(IEnumerable<Project> objects)
        //{
        //    var filteredObjects =
        //        objects.Where(p => p.Name.StartsWith("bwf", StringComparison.OrdinalIgnoreCase)).ToList();

        //    return GetAllProjects(filteredObjects);
        //}

        private List<ProjectModel> GetAllProjects(IEnumerable<Project> objects, bool bwfProjects)
        {
            var filteredObjects = bwfProjects ?
                objects//.Where(p => p.Name.StartsWith("bwf", StringComparison.OrdinalIgnoreCase)).ToList()
                : objects.Where(p => !(p.Name.StartsWith("bwf", StringComparison.OrdinalIgnoreCase)
               // || p.Name.StartsWith("brady", StringComparison.OrdinalIgnoreCase)
                || p.Name.StartsWith("xfa", StringComparison.OrdinalIgnoreCase)
                || p.Name.StartsWith("additionaltestresources", StringComparison.OrdinalIgnoreCase))).ToList();

            var projects = new List<ProjectModel>();

            foreach (var project in filteredObjects)
            {
                if (project.Kind == ProjectKinds.vsProjectKindSolutionFolder)
                {
                    projects.AddRange(GetAllProjects(project.ProjectItems
                        .OfType<ProjectItem>()
                        .Where(i => i.SubProject != null)
                        .Select(i => i.SubProject), bwfProjects));
                }
                else
                {
                    if (project.Object is VSProject)
                        projects.Add(new ProjectModel((VSProject)project.Object));
                }
            }

            return projects;
        }

        private void AddProjectToSolutionIfNeeded(FromNuGetToProjectTransformation fromNuGetToProjectTransformation)
        {
            if (fromNuGetToProjectTransformation.SelectedMode == NuGetToProjectMode.ProjectPath)
            {
                if (!string.IsNullOrEmpty(fromNuGetToProjectTransformation.ToProjectPath) && File.Exists(fromNuGetToProjectTransformation.ToProjectPath))
                {
                    var project = Application.Solution.AddFromFile(fromNuGetToProjectTransformation.ToProjectPath);
                    var myProject = new ProjectModel((VSProject)project.Object);
                    fromNuGetToProjectTransformation.ToProject = myProject;
                }
                else
                    MessageBox.Show("The project '" + fromNuGetToProjectTransformation.ToProjectPath + "' could not be found. (ignored)", "Project not found", MessageBoxButton.OK, MessageBoxImage.Stop);
            }
        }

        private List<ProjectModel> GetCurrentProjectsToRemove()
        {
            return NonBWFProjects
                .SelectMany(p => p.CurrentToNuGetTransformations.Select(s => s.FromProjectName))
                .Select(name => NonBWFProjects.SingleOrDefault(p => p.Name == name))
                .ToList();
        }

        private void RemoveProjectsFromSolution(List<ProjectModel> projectsToDelete)
        {
            if (RemoveProjects)
            {
                var pathsOfProjectsToRemove = projectsToDelete.Select(p => p.Path);
                var projectBuildOrder = ProjectDependencyResolver.GetBuildOrder(pathsOfProjectsToRemove).ToList();
                var orderedProjectsToRemove = projectsToDelete
                    .OrderByDescending(p => projectBuildOrder.IndexOf(p.Path)).Distinct().ToList();

                foreach (var project in orderedProjectsToRemove)
                {
                    if (project != null)
                        project.RemoveFromSolution(Application.Solution);
                }
            }
        }
    }
}
