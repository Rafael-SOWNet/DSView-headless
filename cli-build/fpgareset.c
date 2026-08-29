#include <stdio.h>
#include <string.h>
#include <libusb-1.0/libusb.h>

#define CMD_CTL_WR 0xb0
#define CMD_CTL_RD_PRE 0xb1
#define CMD_CTL_RD 0xb2
#define DSL_CTL_HW_STATUS 2
#define DSL_CTL_PROG_B 3
#define DSL_CTL_LED 5
#define bmWR_PROG_B (1 << 2)
#define bmLED_GREEN (1 << 0)
#define bmLED_RED (1 << 1)
#define bmFPGA_DONE (1 << 6)
#define bmFPGA_INIT_B (1 << 5)

#pragma pack(push,1)
struct ctl_header { unsigned char dest; unsigned short offset; unsigned char size; };
struct ctl_wr_cmd { struct ctl_header header; unsigned char data[60]; };
#pragma pack(pop)

static int wr(libusb_device_handle *h, unsigned char dest, unsigned char val) {
    struct ctl_wr_cmd cmd; memset(&cmd,0,sizeof(cmd));
    cmd.header.dest = dest; cmd.header.offset = 0; cmd.header.size = 1;
    cmd.data[0] = val;
    return libusb_control_transfer(h, LIBUSB_REQUEST_TYPE_VENDOR|LIBUSB_ENDPOINT_OUT,
        CMD_CTL_WR, 0, 0, (unsigned char*)&cmd, 1+sizeof(struct ctl_header), 3000);
}
static unsigned char rdstatus(libusb_device_handle *h) {
    unsigned char s = 0;
    struct ctl_header rh = { DSL_CTL_HW_STATUS, 0, 1 };
    libusb_control_transfer(h, LIBUSB_REQUEST_TYPE_VENDOR|LIBUSB_ENDPOINT_OUT,
        CMD_CTL_RD_PRE, 0, 0, (unsigned char*)&rh, sizeof(rh), 3000);
    libusb_control_transfer(h, LIBUSB_REQUEST_TYPE_VENDOR|LIBUSB_ENDPOINT_IN,
        CMD_CTL_RD, 0, 0, &s, 1, 3000);
    return s;
}

int main(void) {
    libusb_context *ctx = NULL;
    libusb_init(&ctx);
    libusb_device_handle *h = libusb_open_device_with_vid_pid(ctx, 0x2A0E, 0x002A);
    if (!h) { printf("device not found\n"); return 1; }
    libusb_claim_interface(h, 0);

    printf("before: hw_status=0x%02x FPGA_DONE=%d\n",
           rdstatus(h), !!(rdstatus(h) & bmFPGA_DONE));

    printf("assert PROG_B low  -> %d\n", wr(h, DSL_CTL_PROG_B, (unsigned char)~bmWR_PROG_B));
    printf("leds off           -> %d\n", wr(h, DSL_CTL_LED, (unsigned char)(~bmLED_GREEN & ~bmLED_RED)));

    unsigned char s = rdstatus(h);
    printf("after PROG_B low: hw_status=0x%02x FPGA_DONE=%d INIT_B=%d\n",
           s, !!(s & bmFPGA_DONE), !!(s & bmFPGA_INIT_B));

    libusb_release_interface(h, 0);
    libusb_close(h);
    libusb_exit(ctx);
    return 0;
}
