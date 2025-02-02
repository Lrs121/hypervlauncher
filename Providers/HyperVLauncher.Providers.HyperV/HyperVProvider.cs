﻿using System;
using System.Management;
using System.Diagnostics;
using System.Collections.Generic;

using System.Threading;
using System.Threading.Tasks;

using HyperVLauncher.Contracts.Enums;
using HyperVLauncher.Contracts.Models;
using HyperVLauncher.Contracts.Interfaces;

using HyperVLauncher.Providers.Tracing;

using HyperVLauncher.Providers.HyperV.Mappers;
using HyperVLauncher.Providers.HyperV.Extensions;
using HyperVLauncher.Providers.HyperV.Contracts.Enums;

namespace HyperVLauncher.Providers.HyperV
{
    public class HyperVProvider : IHyperVProvider
    {
        public Func<VirtualMachine, Task>? OnVirtualMachineCreated { get; set; }
        public Func<VirtualMachine, Task>? OnVirtualMachineDeleted { get; set; }

        private const string _virtualizationScope = "\\\\.\\root\\virtualization\\v2";

        public IEnumerable<VirtualMachine> GetVirtualMachineList()
        {
            using var searcher = new ManagementObjectSearcher(
                _virtualizationScope,
                "SELECT * FROM Msvm_ComputerSystem WHERE Description='Microsoft Virtual Machine'");

            foreach (var queryObj in searcher.Get())
            {
                var vmId = queryObj["Name"].ToString();
                var vmName = queryObj["ElementName"].ToString();

                if (!string.IsNullOrEmpty(vmId) &&
                    !string.IsNullOrEmpty(vmName))
                {
                    yield return new VirtualMachine(vmId, vmName);
                }
            }
        }

        public VmState GetVirtualMachineState(string vmId)
        {
            using var vmObject = GetVmObject(vmId);

            if (vmObject == null)
            {
                return VmState.Unknown;
            }

            var wmiVmState = (WmiVmState)(ushort)vmObject.GetPropertyValue("EnabledState");

            return wmiVmState.ToVmState();
        }

        public void StartVirtualMachine(string vmId)
        {
            using var vmObject = GetVmObject(vmId);

            if (vmObject == null)
            {
                return;
            }

            if ((ushort)vmObject.GetPropertyValue("EnabledState") == (UInt16)WmiVmState.Started)
            {
                return;
            }

            using var inParams = vmObject.GetMethodParameters("RequestStateChange");

            inParams["RequestedState"] = WmiVmState.Started;

            using var _ = vmObject.InvokeMethod(
                "RequestStateChange",
                inParams,
                null);
        }

        public void PauseVirtualMachine(string vmId)
        {
            using var vmObject = GetVmObject(vmId);

            if (vmObject == null)
            {
                return;
            }

            using var inParams = vmObject.GetMethodParameters("RequestStateChange");

            inParams["RequestedState"] = WmiVmState.Saved;

            using var outParams = vmObject.InvokeMethod(
                "RequestStateChange",
                inParams,
                null);

            WaitForJobToFinish(outParams);
        }

        public void TurnOffVirtualMachine(string vmId)
        {
            using var vmObject = GetVmObject(vmId);

            if (vmObject == null)
            {
                return;
            }

            using var inParams = vmObject.GetMethodParameters("RequestStateChange");

            inParams["RequestedState"] = WmiVmState.Stopped;

            using var outParams = vmObject.InvokeMethod(
                "RequestStateChange",
                inParams,
                null);

            WaitForJobToFinish(outParams);
        }

        public void ShutdownVirtualMachine(string vmId)
        {
            using var vmObject = GetVmObject(vmId);

            if (vmObject is null)
            {
                return;
            }

            var relPath = vmObject.GetPropertyValue("__RELPATH").ToString();

            if (relPath is null)
            {
                return;
            }

            using var shutdownComponent = GetVmShutdownComponent(relPath);

            if (shutdownComponent is null)
            {
                return;
            }

            using var inParams = shutdownComponent.GetMethodParameters("InitiateShutdown");

            inParams["Force"] = true;
            inParams["Reason"] = "Hyper-V Launcher shutdown.";

            using var outParams = shutdownComponent.InvokeMethod(
                "InitiateShutdown",
                inParams,
                null);

            try
            {
                WaitForJobToFinish(outParams);
            }
            catch (Exception ex) when (ex.Message.Contains("32768"))
            {
                Tracer.Warning("Virtual Machine shutdown failed. Trying to turn it off...");

                TurnOffVirtualMachine(vmId);
            }
        }

        public string GetVirtualMachineName(string vmId)
        {
            using var vmObject = GetVmObject(vmId);

            if (vmObject == null)
            {
                throw new InvalidOperationException($"Virtual machine {vmId} was not found.");
            }

            var vmName = vmObject["ElementName"].ToString();

            if (string.IsNullOrEmpty(vmName))
            {
                throw new InvalidOperationException($"Could not find name for virtual machine {vmId}.");
            }

            return vmName;
        }

        private static ManagementObject? GetVmObject(string vmId)
        {
            using var searcher = new ManagementObjectSearcher(
                _virtualizationScope, 
                $"SELECT * FROM Msvm_ComputerSystem WHERE Name = \"{vmId}\"");

            using var searchResults = searcher.Get();

            foreach (ManagementObject vmObject in searchResults)
            {
                return vmObject;
            }

            return null;
        }

        public string[]? GetVirtualMachineIpAddresses(string vmId)
        {
            using var vmObject = GetVmObject(vmId);

            if (vmObject is null)
            {
                return null;
            }

            using var systemSettingsData = vmObject.GetRelated(
                    "Msvm_VirtualSystemSettingData", 
                    "Msvm_SettingsDefineState", 
                    null, 
                    null, 
                    "SettingData", 
                    "ManagedElement", 
                    false, 
                    null)
                .FirstOrDefault();

            if (systemSettingsData is null)
            {
                return null;
            }

            using var ethernetPortSettingsData = systemSettingsData
                .GetRelated("Msvm_SyntheticEthernetPortSettingData")
                .FirstOrDefault();

            if (ethernetPortSettingsData is null)
            {
                return null;
            }

            using var guestNetworkAdapterConfig = ethernetPortSettingsData
                .GetRelated("Msvm_GuestNetworkAdapterConfiguration")
                .FirstOrDefault();

            if (guestNetworkAdapterConfig is null)
            {
                return null;
            }

            var ipAddressesObject = guestNetworkAdapterConfig["IPAddresses"];

            if (ipAddressesObject is not string[] ipAddresses)
            {
                return null;
            }

            return ipAddresses;
        }

        private static ManagementObject? GetVmShutdownComponent(string relPath)
        {
            using var searcher = new ManagementObjectSearcher(
                _virtualizationScope, 
                $"ASSOCIATORS OF {{{relPath}}} WHERE AssocClass=Msvm_SystemDevice ResultClass=Msvm_ShutdownComponent");

            using var searchResults = searcher.Get();

            foreach (ManagementObject shutdownComponent in searchResults)
            {
                return shutdownComponent;
            }

            return null;
        }

        public Process? ConnectVirtualMachine(string vmId)
        {
            var startInfo = new ProcessStartInfo("vmconnect.exe", $"{Environment.MachineName} -G \"{vmId}\"");

            return Process.Start(startInfo);
        }

        private static void WaitForJobToFinish(ManagementBaseObject outParams)
        {
            var returnValue = (UInt32)outParams["ReturnValue"];

            switch (returnValue)
            {
                case WmiReturnCode.Started:
                    WaitForStartedJobToFinish(outParams);
                    break;

                case WmiReturnCode.Completed:
                    return;

                default:
                    throw new InvalidOperationException($"WMI operation failed with return value {returnValue}.");
            }
        }

        private static void WaitForStartedJobToFinish(ManagementBaseObject outParams)
        {
            var jobPath = (string)outParams["Job"];

            using var job = new ManagementObject(_virtualizationScope, jobPath, null);

            job.Get();

            while ((UInt16)job["JobState"] == WmiJobState.Starting
                || (UInt16)job["JobState"] == WmiJobState.Running)
            {
                Thread.Sleep(1000);

                job.Get();
            }

            var finalJobState = (UInt16)job["JobState"];

            if (finalJobState != WmiJobState.Completed)
            {
                var jobErrorCode = (UInt16)job["ErrorCode"];
                var jobErrorDescription = (string)job["ErrorDescription"];

                throw new InvalidOperationException($"WMI operation failed. Error code: {jobErrorCode}, Error description: {jobErrorDescription}");
            }
        }

        public void StartVirtualMachineCreatedMonitor(CancellationToken cancellationToken)
        {
            Tracer.Info("Starting Virtual Machine created event watcher...");

            var watcher = new ManagementEventWatcher(
                _virtualizationScope,
                "SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Msvm_ComputerSystem'");

            watcher.Stopped += Watcher_CreateStopped;
            watcher.EventArrived += Watcher_OnCreatedEvent;

            cancellationToken.Register(
                () =>
                {
                    Tracer.Debug("Stopping Virtual Machine created event watcher...");

                    watcher.Stop();
                });

            watcher.Start();

            Tracer.Info("Virtual Machine created event watcher started.");
        }

        public void StartVirtualMachineDeletedMonitor(CancellationToken cancellationToken)
        {
            Tracer.Info("Starting Virtual Machine deleted event watcher...");

            var watcher = new ManagementEventWatcher(
                _virtualizationScope,
                "SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Msvm_ComputerSystem'");

            watcher.Stopped += Watcher_DeleteStopped;
            watcher.EventArrived += Watcher_OnDeletedEvent;

            cancellationToken.Register(
                () =>
                {
                    Tracer.Debug("Stopping Virtual Machine deleted event watcher...");

                    watcher.Stop();
                });

            watcher.Start();

            Tracer.Info("Virtual Machine deleted event watcher started.");
        }

        private void Watcher_CreateStopped(object sender, StoppedEventArgs e)
        {
            Tracer.Info("Virtual Machine created event watcher stopped.");
        }

        private void Watcher_DeleteStopped(object sender, StoppedEventArgs e)
        {
            Tracer.Info("Virtual Machine deleted event watcher stopped.");
        }

        private void Watcher_OnCreatedEvent(object sender, EventArrivedEventArgs e)
        {
            var eventObject = e.NewEvent;

            if (eventObject["TargetInstance"] is not ManagementBaseObject vmObject)
            {
                Tracer.Warning("Received invalid Virtual Machine event.");

                return;
            }

            var vmId = vmObject["Name"].ToString();
            var vmName = vmObject["ElementName"].ToString();

            Tracer.Info($"Virtual Machine created event: {vmId} - {vmName}");

            if (string.IsNullOrEmpty(vmId) ||
                string.IsNullOrEmpty(vmName))
            {
                Tracer.Warning("Received invalid Virtual Machine event data.");

                return;
            }

            if (OnVirtualMachineCreated is not null)
            {
                OnVirtualMachineCreated(new VirtualMachine(vmId, vmName));
            }
        }

        private void Watcher_OnDeletedEvent(object sender, EventArrivedEventArgs e)
        {
            var eventObject = e.NewEvent;

            if (eventObject["TargetInstance"] is not ManagementBaseObject vmObject)
            {
                Tracer.Warning("Received invalid Virtual Machine event.");

                return;
            }

            var vmId = vmObject["Name"].ToString();
            var vmName = vmObject["ElementName"].ToString();

            Tracer.Info($"Virtual Machine created event: {vmId} - {vmName}");

            if (string.IsNullOrEmpty(vmId) ||
                string.IsNullOrEmpty(vmName))
            {
                Tracer.Warning("Received invalid Virtual Machine event data.");

                return;
            }

            if (OnVirtualMachineDeleted is not null)
            {
                OnVirtualMachineDeleted(new VirtualMachine(vmId, vmName));
            }
        }
    }
}
