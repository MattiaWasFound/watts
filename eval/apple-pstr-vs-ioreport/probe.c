// Sudoless power probe for Apple Silicon: IOReport "Energy Model" (CPU/GPU/ANE/DRAM)
// and the SMC "PSTR" key (whole-system power). Prints one line per interval.
#include <CoreFoundation/CoreFoundation.h>
#include <IOKit/IOKitLib.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

typedef struct IOReportSubscription *IOReportSubscriptionRef;
CFDictionaryRef IOReportCopyChannelsInGroup(CFStringRef, CFStringRef, uint64_t, uint64_t, uint64_t);
IOReportSubscriptionRef IOReportCreateSubscription(void *, CFMutableDictionaryRef, CFMutableDictionaryRef *, uint64_t, CFTypeRef);
CFDictionaryRef IOReportCreateSamples(IOReportSubscriptionRef, CFMutableDictionaryRef, CFTypeRef);
CFDictionaryRef IOReportCreateSamplesDelta(CFDictionaryRef, CFDictionaryRef, CFTypeRef);
int64_t IOReportSimpleGetIntegerValue(CFDictionaryRef, int32_t);
CFStringRef IOReportChannelGetChannelName(CFDictionaryRef);
CFStringRef IOReportChannelGetUnitLabel(CFDictionaryRef);
void IOReportIterate(CFDictionaryRef, int (^)(CFDictionaryRef));

typedef struct { uint32_t key; uint8_t pad0[22]; uint32_t dataSize; uint32_t dataType; uint8_t dataAttr;
  uint8_t pad1[3]; uint8_t result; uint8_t status; uint8_t data8; uint32_t data32; uint8_t bytes[32]; } SMCParam;
static uint32_t k4(const char *s){return (s[0]<<24)|(s[1]<<16)|(s[2]<<8)|s[3];}
static io_connect_t smc;
static int smcCall(SMCParam *in, SMCParam *out){size_t n=sizeof(SMCParam);
  return IOConnectCallStructMethod(smc,2,in,sizeof(SMCParam),out,&n);}
static float smcFloat(const char *key){SMCParam in={0},out={0};in.key=k4(key);in.data8=9;
  if(smcCall(&in,&out)||out.result) return -1; uint32_t sz=out.dataSize;
  memset(&in,0,sizeof in);in.key=k4(key);in.dataSize=sz;in.data8=5;memset(&out,0,sizeof out);
  if(smcCall(&in,&out)||out.result) return -1; float f; memcpy(&f,out.bytes,4); return f;}

int main(int argc,char**argv){
  int secs = argc>1?atoi(argv[1]):10; double dt = argc>2?atof(argv[2]):1.0;
  io_service_t s=IOServiceGetMatchingService(kIOMainPortDefault,IOServiceMatching("AppleSMC"));
  int smcOk = s && IOServiceOpen(s,mach_task_self(),0,&smc)==KERN_SUCCESS;
  CFDictionaryRef ch=IOReportCopyChannelsInGroup(CFSTR("Energy Model"),NULL,0,0,0);
  CFMutableDictionaryRef chm=CFDictionaryCreateMutableCopy(NULL,0,ch), sub=NULL;
  IOReportSubscriptionRef rs=IOReportCreateSubscription(NULL,chm,&sub,0,NULL);
  CFDictionaryRef prev=IOReportCreateSamples(rs,sub,NULL);
  if(getenv("LIST")) IOReportIterate(prev,^int(CFDictionaryRef c){char n[128],u[16]="";CFStringGetCString(IOReportChannelGetChannelName(c),n,128,kCFStringEncodingUTF8);CFStringRef ul=IOReportChannelGetUnitLabel(c);if(ul)CFStringGetCString(ul,u,16,kCFStringEncodingUTF8);printf("  ch %s [%s]\n",n,u);return 0;});
  printf("t,soc_w,cpu_w,gpu_w,pstr_w\n");
  for(int i=0;i<secs/dt;i++){ usleep(dt*1e6);
    CFDictionaryRef cur=IOReportCreateSamples(rs,sub,NULL), d=IOReportCreateSamplesDelta(prev,cur,NULL);
    __block double soc=0,cpu=0,gpu=0;
    IOReportIterate(d,^int(CFDictionaryRef c){ char name[128],unit[16];
      CFStringGetCString(IOReportChannelGetChannelName(c),name,128,kCFStringEncodingUTF8);
      CFStringRef u=IOReportChannelGetUnitLabel(c); unit[0]=0; if(u) CFStringGetCString(u,unit,16,kCFStringEncodingUTF8);
      double j=IOReportSimpleGetIntegerValue(c,0);
      if(!strcmp(unit,"mJ")) j/=1e3; else if(!strcmp(unit,"uJ")) j/=1e6; else if(!strcmp(unit,"nJ")) j/=1e9; else return 0;
      double w=j/dt;
      if(strstr(name,"CPU Energy")) cpu+=w; else if(!strcmp(name,"GPU Energy")) gpu+=w;
      if(strstr(name,"CPU Energy")||!strcmp(name,"GPU Energy")||!strcmp(name,"ANE")||strstr(name,"DRAM")) soc+=w;
      return 0;});
    printf("%.1f,%.2f,%.2f,%.2f,%.2f\n",(i+1)*dt,soc,cpu,gpu,smcOk?smcFloat("PSTR"):-1); fflush(stdout);
    CFRelease(d); CFRelease(prev); prev=cur; }
}
