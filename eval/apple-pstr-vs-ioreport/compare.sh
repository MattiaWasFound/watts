#!/bin/bash
# Runs a simple CPU-load formula (8 W + 55 W x busy, from `top`) beside the SMC PSTR
# whole-system reading, through idle / full-CPU-load / idle phases. Writes probe-<phase>.csv,
# est-<phase>.txt and results-<TAG>.txt next to the script.
cd "$(dirname "$0")"
phase() { name=$1; secs=$2
  ./probe $secs 1 > probe-$name.csv &
  pp=$!
  top -l $((secs/2+1)) -s 2 -n 0 | grep "CPU usage" | awk '{gsub("%","",$7); print 8+55*(100-$7)/100}' > est-$name.txt &
  tp=$!
  wait $pp $tp
}
phase idle1 20
pids=(); for i in $(seq 1 $(sysctl -n hw.ncpu)); do ${LOAD:-yes} >/dev/null 2>&1 & pids+=($!); done
phase load 20
kill ${pids[@]}
phase idle2 20
for p in idle1 load idle2; do
  real=$(awk -F, 'NR>1&&$5>0{s+=$5;n++}END{printf "%.1f",s/n}' probe-$p.csv)
  soc=$(awk -F, 'NR>1{s+=$2;n++}END{printf "%.1f",s/n}' probe-$p.csv)
  est=$(tail -n +2 est-$p.txt | awk '{s+=$1;n++}END{printf "%.1f",s/n}')
  echo "$p,real_pstr_w=$real,soc_w=$soc,formula_w=$est"
done | tee results-${TAG:-yes}.txt
