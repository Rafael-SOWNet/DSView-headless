/*
 * Does PROG_B actually reach the FPGA?
 *
 * On a Xilinx-style part, driving PROG_B low must pull INIT_B low while the configuration
 * memory is cleared, then INIT_B rises again once the part is ready for a new bitstream.
 *
 *   INIT_B follows PROG_B  -> the config pins work; the FPGA itself is refusing to finish
 *   INIT_B never moves     -> PROG_B is not getting through (FX3 GPIO stuck) and the USB
 *                             controller, not the FPGA, is what needs resetting
 */
#include <stdio.h>
#include <string.h>
#include <libusb-1.0/libusb.h>

#define CMD_CTL_WR 0xb0
#define CMD_CTL_RD_PRE 0xb1
#define CMD_CTL_RD 0xb2
#define DSL_CTL_HW_STATUS 2
#define DSL_CTL_PROG_B 3
#define bmWR_PROG_B (1 << 2)
#define bmFPGA_DONE (1 << 6)
#define bmFPGA_INIT_B (1 << 5)
#define bmGPIF_DONE (1 << 7)

#pragma pack(push,1)
struct ctl_header { unsigned char dest; unsigned short offset; unsigned char size; };
struct ctl_wr_cmd { struct ctl_header header; unsigned char data[60]; };
#pragma pack(pop)

static libusb_device_handle *h;

static int wr1(unsigned char dest, unsigned char v) {
    struct ctl_wr_cmd cmd; memset(&cmd, 0, sizeof(cmd));
    cmd.header.dest = dest; cmd.header.offset = 0; cmd.header.size = 1; cmd.data[0] = v;
    return libusb_control_transfer(h, LIBUSB_REQUEST_TYPE_VENDOR | LIBUSB_ENDPOINT_OUT,
        CMD_CTL_WR, 0, 0, (unsigned char*)&cmd, 1 + sizeof(struct ctl_header), 3000);
}

static unsigned char rdstat(void) {
    unsigned char s = 0;
    struct ctl_header rh = { DSL_CTL_HW_STATUS, 0, 1 };
    libusb_control_transfer(h, LIBUSB_REQUEST_TYPE_VENDOR | LIBUSB_ENDPOINT_OUT,
        CMD_CTL_RD_PRE, 0, 0, (unsigned char*)&rh, sizeof(rh), 3000);
    libusb_control_transfer(h, LIBUSB_REQUEST_TYPE_VENDOR | LIBUSB_ENDPOINT_IN,
        CMD_CTL_RD, 0, 0, &s, 1, 3000);
    return s;
}

static void show(const char *label) {
    unsigned char s = rdstat();
    printf("%-28s status=0x%02x  FPGA_DONE=%d INIT_B=%d GPIF_DONE=%d\n",
           label, s, !!(s & bmFPGA_DONE), !!(s & bmFPGA_INIT_B), !!(s & bmGPIF_DONE));
}

int main(void) {
    libusb_context *ctx = NULL;
    libusb_init(&ctx);
    h = libusb_open_device_with_vid_pid(ctx, 0x2A0E, 0x002A);
    if (!h) { printf("device not found\n"); return 1; }
    libusb_claim_interface(h, 0);

    show("initial");
    wr1(DSL_CTL_PROG_B, (unsigned char)~bmWR_PROG_B);
    show("PROG_B low (x1)");
    show("PROG_B low (x2)");
    show("PROG_B low (x3)");
    wr1(DSL_CTL_PROG_B, bmWR_PROG_B);
    show("PROG_B high (x1)");
    show("PROG_B high (x2)");

    libusb_release_interface(h, 0);
    libusb_close(h);
    libusb_exit(ctx);
    return 0;
}
