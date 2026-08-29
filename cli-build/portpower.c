#include <windows.h>
#include <stdio.h>
#include <stdlib.h>

/*
 * Does the port the DSLogic sits on actually have per-port VBUS (power) control?
 *
 * Background: after clearing the FPGA's config via PROG_B the device would not reconfigure, and
 * every software-level reset that does NOT drop VBUS failed to recover it:
 *   libusb_reset_device()  - USB protocol reset only
 *   shutdown /r            - Windows keeps USB powered across a warm reboot
 *   Disable/Enable-PnpDevice - driver reload, no VBUS drop
 *
 * A hub reports its power-switching capability in wHubCharacteristics bits 0..1:
 *   00 = ganged (all ports switched together)
 *   01 = individual per-port switching
 *   1x = no power switching at all (VBUS permanently on)
 * IOCTL_USB_HUB_CYCLE_PORT is the documented way to ask the hub driver to cycle one port.
 */

#define FILE_DEVICE_USB 0x22
#define CTL(code) CTL_CODE(FILE_DEVICE_USB, code, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_USB_GET_NODE_INFORMATION    CTL(258)
#define IOCTL_USB_HUB_CYCLE_PORT          CTL(273)
#define IOCTL_USB_GET_HUB_CAPABILITIES_EX CTL(271)

#pragma pack(push,1)
typedef struct { UCHAR bDescriptorLength, bDescriptorType, bNumberOfPorts;
                 USHORT wHubCharacteristics; UCHAR bPowerOnToPowerGood, bHubControlCurrent;
                 UCHAR bRemoveAndPowerMask[64]; } HUBDESC;
typedef struct { HUBDESC HubDescriptor; BOOLEAN HubIsBusPowered; } HUBINFO;
typedef struct { ULONG NodeType; HUBINFO HubInformation; } NODEINFO;
typedef struct { ULONG ConnectionIndex; ULONG StatusReturned; } CYCLEPARAMS;
typedef struct { ULONG CapabilityFlags; } HUBCAPEX;
#pragma pack(pop)

int main(int argc, char **argv)
{
    int port = (argc > 1) ? atoi(argv[1]) : 15;
    const char *path = (argc > 2) ? argv[2]
        : "\\\\?\\usb#root_hub30#4&acbee28&0&0#{f18a0e88-c30c-11d0-8815-00a0c906bed8}";

    printf("hub path: %s\nport: %d\n\n", path, port);

    HANDLE h = CreateFileA(path, GENERIC_WRITE, FILE_SHARE_WRITE,
                           NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) {
        printf("open hub failed: %lu\n", GetLastError());
        return 1;
    }
    printf("hub opened OK\n");

    DWORD ret = 0;
    NODEINFO ni;
    ZeroMemory(&ni, sizeof(ni));
    if (DeviceIoControl(h, IOCTL_USB_GET_NODE_INFORMATION,
                        &ni, sizeof(ni), &ni, sizeof(ni), &ret, NULL)) {
        USHORT c = ni.HubInformation.HubDescriptor.wHubCharacteristics;
        int pwr = c & 0x03;
        printf("ports = %u\n", ni.HubInformation.HubDescriptor.bNumberOfPorts);
        printf("wHubCharacteristics = 0x%04x\n", c);
        printf("power switching = %d (%s)\n", pwr,
               pwr == 0 ? "GANGED - all ports switched together" :
               pwr == 1 ? "INDIVIDUAL - real per-port VBUS control" :
                          "NONE - VBUS permanently on, cannot be cut in software");
        printf("hub is bus powered = %d\n", ni.HubInformation.HubIsBusPowered);
    } else {
        printf("GET_NODE_INFORMATION failed: %lu\n", GetLastError());
    }

    HUBCAPEX cap;
    ZeroMemory(&cap, sizeof(cap));
    if (DeviceIoControl(h, IOCTL_USB_GET_HUB_CAPABILITIES_EX,
                        &cap, sizeof(cap), &cap, sizeof(cap), &ret, NULL))
        printf("hub capability flags = 0x%08lx\n", cap.CapabilityFlags);
    else
        printf("GET_HUB_CAPABILITIES_EX failed: %lu\n", GetLastError());

    printf("\nattempting IOCTL_USB_HUB_CYCLE_PORT on port %d ...\n", port);
    CYCLEPARAMS cp;
    ZeroMemory(&cp, sizeof(cp));
    cp.ConnectionIndex = port;
    if (DeviceIoControl(h, IOCTL_USB_HUB_CYCLE_PORT,
                        &cp, sizeof(cp), &cp, sizeof(cp), &ret, NULL))
        printf("CYCLE_PORT OK, StatusReturned=%lu\n", cp.StatusReturned);
    else
        printf("CYCLE_PORT failed: %lu (5 = access denied/needs elevation, "
               "50 = not supported)\n", GetLastError());

    CloseHandle(h);
    return 0;
}
