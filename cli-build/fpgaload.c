#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <libusb-1.0/libusb.h>

#define CMD_CTL_WR 0xb0
#define CMD_CTL_RD_PRE 0xb1
#define CMD_CTL_RD 0xb2
#define DSL_CTL_HW_STATUS 2
#define DSL_CTL_PROG_B 3
#define DSL_CTL_LED 5
#define DSL_CTL_INTRDY 6
#define DSL_CTL_WORDWIDE 7
#define DSL_CTL_BULK_WR 10
#define bmWR_PROG_B (1<<2)
#define bmWR_INTRDY (1<<7)
#define bmWR_WORDWIDE (1<<0)
#define bmLED_GREEN (1<<0)
#define bmLED_RED (1<<1)
#define bmFPGA_DONE (1<<6)
#define bmFPGA_INIT_B (1<<5)
#define bmGPIF_DONE (1<<7)

#pragma pack(push,1)
struct ctl_header { unsigned char dest; unsigned short offset; unsigned char size; };
struct ctl_wr_cmd { struct ctl_header header; unsigned char data[60]; };
#pragma pack(pop)

static libusb_device_handle *h;

static int wrn(unsigned char dest, unsigned char *d, int n) {
    struct ctl_wr_cmd cmd; memset(&cmd,0,sizeof(cmd));
    cmd.header.dest = dest; cmd.header.offset = 0; cmd.header.size = n;
    memcpy(cmd.data, d, n);
    return libusb_control_transfer(h, LIBUSB_REQUEST_TYPE_VENDOR|LIBUSB_ENDPOINT_OUT,
        CMD_CTL_WR, 0, 0, (unsigned char*)&cmd, n+sizeof(struct ctl_header), 3000);
}
static int wr1(unsigned char dest, unsigned char v){ return wrn(dest,&v,1); }
static unsigned char rdstat(void) {
    unsigned char s=0; struct ctl_header rh = { DSL_CTL_HW_STATUS, 0, 1 };
    libusb_control_transfer(h, LIBUSB_REQUEST_TYPE_VENDOR|LIBUSB_ENDPOINT_OUT,
        CMD_CTL_RD_PRE,0,0,(unsigned char*)&rh,sizeof(rh),3000);
    libusb_control_transfer(h, LIBUSB_REQUEST_TYPE_VENDOR|LIBUSB_ENDPOINT_IN,
        CMD_CTL_RD,0,0,&s,1,3000);
    return s;
}
static int waitbit(unsigned char mask, int want, int tries, const char *what) {
    for (int i=0;i<tries;i++){
        unsigned char s = rdstat();
        if (!!(s & mask) == want) { printf("  %s ok after %d polls (status 0x%02x)\n", what, i, s); return 1; }
    }
    printf("  TIMEOUT waiting for %s (status 0x%02x)\n", what, rdstat());
    return 0;
}

int main(int argc, char **argv) {
    const char *bit = argc>1?argv[1]:"C:\Program Files\DSView\res\DSLogicU3Pro16.bin";
    libusb_context *ctx=NULL; libusb_init(&ctx);
    h = libusb_open_device_with_vid_pid(ctx,0x2A0E,0x002A);
    if(!h){printf("no device\n");return 1;}
    libusb_claim_interface(h,0);
    libusb_clear_halt(h, 2|LIBUSB_ENDPOINT_OUT);
    libusb_clear_halt(h, 6|LIBUSB_ENDPOINT_IN);
    printf("start status=0x%02x\n", rdstat());

    FILE *f=fopen(bit,"rb");
    if(!f){printf("cannot open %s\n",bit);return 1;}
    fseek(f,0,SEEK_END); long fsz=ftell(f); fseek(f,0,SEEK_SET);
    unsigned char *buf=malloc(fsz);
    long got=fread(buf,1,fsz,f); fclose(f);
    printf("bitstream %s: %ld bytes read\n", bit, got);

    printf("step0 PROG_B low  -> %d\n", wr1(DSL_CTL_PROG_B,(unsigned char)~bmWR_PROG_B));
    printf("step1 leds off    -> %d\n", wr1(DSL_CTL_LED,(unsigned char)(~bmLED_GREEN & ~bmLED_RED)));
    printf("step2 PROG_B high -> %d\n", wr1(DSL_CTL_PROG_B,bmWR_PROG_B));
    if(!waitbit(bmFPGA_INIT_B,1,2000,"INIT_B high")) return 2;
    printf("step4 INTRDY low  -> %d\n", wr1(DSL_CTL_INTRDY,(unsigned char)~bmWR_INTRDY));

    unsigned char szb[3] = {(unsigned char)got,(unsigned char)(got>>8),(unsigned char)(got>>16)};
    printf("step4b BULK_WR hdr-> %d\n", wrn(DSL_CTL_BULK_WR, szb, 3));

    int transferred=0;
    int r = libusb_bulk_transfer(h, 2|LIBUSB_ENDPOINT_OUT, buf, got, &transferred, 5000);
    printf("step5 bulk xfer   -> r=%d (%s) transferred=%d/%ld\n",
           r, r<0?libusb_error_name(r):"OK", transferred, got);
    if (r<0) return 3;

    printf("step6 INTRDY high -> %d\n", wr1(DSL_CTL_INTRDY,bmWR_INTRDY));
    if(!waitbit(bmGPIF_DONE,1,2000,"GPIF_DONE")) return 4;
    if(!waitbit(bmFPGA_DONE,1,5000,"FPGA_DONE")) return 5;
    printf("step10 green led  -> %d\n", wr1(DSL_CTL_LED,bmLED_GREEN));
    printf("step10b WORDWIDE  -> %d\n", wr1(DSL_CTL_WORDWIDE,bmWR_WORDWIDE));
    printf("FINAL status=0x%02x\n", rdstat());
    printf("SUCCESS: FPGA configured\n");
    libusb_release_interface(h,0); libusb_close(h); libusb_exit(ctx);
    return 0;
}
