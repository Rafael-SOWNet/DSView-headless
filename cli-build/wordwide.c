#include <stdio.h>
#include <string.h>
#include <libusb-1.0/libusb.h>

#define CMD_CTL_WR 0xb0
#define CMD_CTL_RD_PRE 0xb1
#define CMD_CTL_RD 0xb2
#define DSL_CTL_HW_STATUS 2
#define DSL_CTL_WORDWIDE 7
#define bmWR_WORDWIDE (1 << 0)
#define bmFPGA_DONE (1 << 6)
#define bmGPIF_DONE (1 << 7)

#pragma pack(push,1)
struct ctl_header { unsigned char dest; unsigned short offset; unsigned char size; };
struct ctl_wr_cmd { struct ctl_header header; unsigned char data[60]; };
#pragma pack(pop)

int main(void) {
    libusb_context *ctx = NULL;
    libusb_init(&ctx);
    libusb_device_handle *h = libusb_open_device_with_vid_pid(ctx, 0x2A0E, 0x002A);
    if (!h) { printf("device not found\n"); return 1; }
    if (libusb_kernel_driver_active(h, 0) == 1) libusb_detach_kernel_driver(h, 0);
    int r = libusb_claim_interface(h, 0);
    printf("claim_interface -> %d (%s)\n", r, libusb_error_name(r));

    /* read hw status first */
    unsigned char status = 0;
    struct ctl_header rh = { DSL_CTL_HW_STATUS, 0, 1 };
    r = libusb_control_transfer(h, LIBUSB_REQUEST_TYPE_VENDOR|LIBUSB_ENDPOINT_OUT,
            CMD_CTL_RD_PRE, 0, 0, (unsigned char*)&rh, sizeof(rh), 3000);
    printf("rd_pre -> %d\n", r);
    r = libusb_control_transfer(h, LIBUSB_REQUEST_TYPE_VENDOR|LIBUSB_ENDPOINT_IN,
            CMD_CTL_RD, 0, 0, &status, 1, 3000);
    printf("hw_status = 0x%02x (FPGA_DONE=%d GPIF_DONE=%d)\n",
           status, !!(status & bmFPGA_DONE), !!(status & bmGPIF_DONE));

    /* send WORDWIDE */
    struct ctl_wr_cmd cmd;
    memset(&cmd, 0, sizeof(cmd));
    cmd.header.dest = DSL_CTL_WORDWIDE;
    cmd.header.offset = 0;
    cmd.header.size = 1;
    cmd.data[0] = bmWR_WORDWIDE;
    r = libusb_control_transfer(h, LIBUSB_REQUEST_TYPE_VENDOR|LIBUSB_ENDPOINT_OUT,
            CMD_CTL_WR, 0, 0, (unsigned char*)&cmd,
            cmd.header.size + sizeof(struct ctl_header), 3000);
    printf("WORDWIDE write -> %d (%s)\n", r, r<0?libusb_error_name(r):"OK");

    libusb_release_interface(h, 0);
    libusb_close(h);
    libusb_exit(ctx);
    return 0;
}
